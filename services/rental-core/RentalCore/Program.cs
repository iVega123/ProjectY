using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using MotoHub.Data;
using MotoHub.Repositories;
using MotoHub.Services.RabbitMQ;
using Npgsql;
using OpenTelemetry.Trace;
using ProjectY.Shared.Health;
using ProjectY.Shared.Hosting;
using ProjectY.Shared.Idempotency;
using ProjectY.Shared.Messaging;
using ProjectY.Shared.Observability;
using ProjectY.Shared.Security;
using RentalOperations.Repository;
using RentalOperations.Services;
using RentalOperations.Services.RabbitMQService;
using Serilog;
using Serilog.Formatting.Compact;

// Um serviço, um processo, um banco. As duas metades mantêm seus namespaces --
// MotoHub.* e RentalOperations.* -- porque renomeá-los enterraria a fusão num
// diff que ninguém conseguiria revisar. Os nomes entram no passo que dissolve a
// costura entre elas.

if (await HealthProbeCommand.TryRunAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);

var serviceName = builder.Configuration["OTEL_SERVICE_NAME"]
    ?? builder.Configuration["ApplicationName"]
    ?? "rental-core";

builder.Services.AddProjectYTelemetry(
    builder.Configuration,
    serviceName,
    tracing => tracing.AddEntityFrameworkCoreInstrumentation());

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ApplicationName", serviceName)
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .WriteToProjectYTelemetry(builder.Configuration, serviceName)
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddProjectYIdempotency(builder.Configuration, "rental-core");

var rabbit = builder.Configuration.GetSection("RabbitMQ").Get<MotoHub.Configurations.RabbitMQOptions>()
    ?? throw new InvalidOperationException("RabbitMQ configuration is missing.");
builder.Services.AddSingleton(rabbit);

// Uma string de conexão para o serviço inteiro. Motos, aluguéis, outbox e
// inbox estão no mesmo banco -- é isso que permite que criar um aluguel e
// anunciá-lo sejam a mesma transação, e que aposentar uma moto seja uma
// instrução em vez de um protocolo entre dois bancos.
var connectionString = builder.Configuration.GetConnectionString("Postgresql")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgresql is not configured.");
var database = new NpgsqlConnectionStringBuilder(connectionString);
builder.Services
    .AddProjectYHealthChecks()
    .AddTcpDependency("database", database.Host ?? "cockroachdb", database.Port)
    .AddTcpDependency("rabbitmq", rabbit.HostName, 5672);

builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IApplicationDbContext>(services =>
    services.GetRequiredService<ApplicationDbContext>());

// Uma fonte de dados por processo. Cada uma tem seu próprio pool, então criar
// uma por escrita vazaria um pool por escrita; o contêiner descarta esta.
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

// One audience for one service. The gateway sends projecty.rental-core for both
// paths now, so a token minted for either half is accepted by the merged service.
builder.Services.AddGatewayIdentityAuthentication(builder.Configuration, "projecty.rental-core");

builder.Services.AddControllers();
builder.Services.AddAutoMapper(_ => { }, typeof(Program));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<IdempotencyKeyOperationFilter>();
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "RentalCore", Version = "v1" });
});

// The motorcycle half
builder.Services.AddScoped<IMotorcycleRepository, MotorcycleRepository>();
builder.Services.AddScoped<MotoHub.Services.IMotorcycleService, MotoHub.Services.MotorcycleService>();
builder.Services.AddSingleton<MotoHub.Services.IMotorcycleRetirement, MotoHub.Services.MotorcycleRetirement>();
builder.Services.AddScoped<IMessagingPublisherService, MessagingPublisherService>();
builder.Services.AddSingleton(new OutboxRelayOptions
{
    ServiceName = "rental-core",
    HostName = rabbit.HostName,
    VirtualHost = rabbit.VirtualHost,
    UserName = rabbit.UserName,
    Password = rabbit.Password
});
builder.Services.AddSingleton<IOutboxTransport, RabbitMqOutboxTransport>();
builder.Services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
builder.Services.AddHostedService<OutboxRelay<ApplicationDbContext>>();

// The rental half
builder.Services.AddHostedService<RentalKafkaRelay>();
builder.Services.AddHostedService<PricingProjection>();
builder.Services.AddHostedService<RiderProjection>();
builder.Services.AddSingleton<IRiderProjectionStore, SqlRiderProjectionStore>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Messaging:Inbox").Get<InboxOptions>()
        ?? new InboxOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SqlInboxProcessor>();
builder.Services.AddHostedService<InboxRetentionSweeper>();

// A metade de aluguéis pergunta à de motos por chamada de método. Era um
// HttpClient para o próprio processo, com timeout e propagação de identidade
// para si mesmo; a costura entre os domínios continua sendo a interface.
builder.Services.AddScoped<RentalOperations.CrossCutting.Services.IMotorcycleService,
    RentalOperations.CrossCutting.Services.MotorcycleService>();

builder.Services.AddScoped<IRentalRepository, SqlRentalRepository>();
builder.Services.AddScoped<IRentalService, RentalService>();

var app = builder.Build();

if (SwaggerPolicy.IsEnabled(app.Environment, app.Configuration))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// UseHttpsRedirection saiu no #100.
//
// Sem porta HTTPS conhecida ele não redireciona nada: registra um aviso na
// subida e deixa a requisição passar. Ficava como decoração que se lia como
// garantia -- e é a metade pior do achado A4, porque uma garantia declarada e
// não cumprida engana mais do que uma ausente. Quando o ingress do épico 10
// terminar TLS, o que entra no lugar é ForwardedHeaders, para o aplicativo ver
// o esquema original em vez de adivinhá-lo. Ver ADR 0025.

app.UseAuthentication();
app.UseAuthorization();
app.UseProjectYIdempotency();

app.MapControllers();
app.MapProjectYHealthChecks();
app.MapOutboxMetrics<ApplicationDbContext>("rental-core");

app.Run();

public partial class Program { }
