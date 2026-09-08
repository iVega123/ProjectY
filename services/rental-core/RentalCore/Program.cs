using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Trace;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using MotoHub.Data;
using MotoHub.Repositories;
using MotoHub.Services.RabbitMQ;
using Npgsql;
using ProjectY.Shared.Health;
using ProjectY.Shared.Hosting;
using ProjectY.Shared.Idempotency;
using ProjectY.Shared.Messaging;
using ProjectY.Shared.Observability;
using ProjectY.Shared.Security;
using RentalOperations.Data;
using RentalOperations.Repository;
using RentalOperations.Services;
using RentalOperations.Services.RabbitMQService;
using Serilog;
using Serilog.Formatting.Compact;

// One service, one process. The two halves keep their namespaces -- MotoHub.* and
// RentalOperations.* -- because renaming them would bury the merge in a diff nobody
// could review. The names go in the step that dissolves the seam between them.

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
    tracing => tracing.AddEntityFrameworkCoreInstrumentation(),
    MongoTelemetry.ActivitySourceName);

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ApplicationName", serviceName)
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .WriteToProjectYTelemetry(builder.Configuration, serviceName)
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddProjectYIdempotency(builder.Configuration, "rental-core");

// Both halves read the same RabbitMQ section into their own options type. Each is
// registered under its own type, so the classes that ask for one keep working.
var motorcycleRabbit = builder.Configuration.GetSection("RabbitMQ").Get<MotoHub.Configurations.RabbitMQOptions>()
    ?? throw new InvalidOperationException("RabbitMQ configuration is missing.");
var rentalRabbit = builder.Configuration.GetSection("RabbitMQ").Get<RentalOperations.Configurations.RabbitMQOptions>();
builder.Services.AddSingleton(motorcycleRabbit);
builder.Services.AddSingleton(rentalRabbit!);
builder.Services.Configure<RentalOperations.Configurations.RabbitMQOptions>(
    builder.Configuration.GetSection("RabbitMQ"));

var postgresConnection = new NpgsqlConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Postgresql") ?? "Host=postgres;Port=5432");
var mongoDbSettings = builder.Configuration.GetSection("MongoDbSettings");
var mongoUrl = new MongoUrl(mongoDbSettings["ConnectionString"] ?? "mongodb://mongodb:27017");
builder.Services
    .AddProjectYHealthChecks()
    .AddTcpDependency("postgres", postgresConnection.Host ?? "postgres", postgresConnection.Port)
    .AddTcpDependency("mongodb", mongoUrl.Server.Host, mongoUrl.Server.Port)
    .AddTcpDependency("rabbitmq", motorcycleRabbit.HostName, 5672);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgresql")));
builder.Services.AddScoped<IApplicationDbContext>(services =>
    services.GetRequiredService<ApplicationDbContext>());
builder.Services.AddSingleton<MongoDbContext>(_ =>
    new MongoDbContext(mongoDbSettings["ConnectionString"], mongoDbSettings["DatabaseName"]));

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
builder.Services.AddScoped<IMessagingPublisherService, MessagingPublisherService>();
builder.Services.AddSingleton(new OutboxRelayOptions
{
    ServiceName = "rental-core",
    HostName = motorcycleRabbit.HostName,
    VirtualHost = motorcycleRabbit.VirtualHost,
    UserName = motorcycleRabbit.UserName,
    Password = motorcycleRabbit.Password
});
builder.Services.AddSingleton<IOutboxTransport, RabbitMqOutboxTransport>();
builder.Services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
builder.Services.AddHostedService<OutboxRelay<ApplicationDbContext>>();
builder.Services.AddHostedService<MotoHub.Services.MotorcycleProjector>();

// The rental half
builder.Services.AddHostedService<MongoRentalIndexInitializer>();
builder.Services.AddHostedService<RentalKafkaRelay>();
builder.Services.AddHostedService<PricingProjection>();
builder.Services.AddHostedService<RiderProjection>();
builder.Services.AddSingleton<IRiderProjectionStore, MongoRiderProjectionStore>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Messaging:Inbox").Get<MongoInboxOptions>()
        ?? new MongoInboxOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MongoInboxProcessor>();
builder.Services.AddHostedService<MongoInboxInitializer>();
builder.Services.AddSingleton<IRabbitMqService, RabbitMqService>();
builder.Services.AddSingleton<IMessagingConsumerService, MessagingConsumerService>();
builder.Services.AddHostedService<ConsumerHostedService>();

// Still a call, still over HTTP, now to itself. The two halves reach each other
// through the same clients they used across the network, because collapsing them
// into method calls is the step that removes the seam -- and doing it here would
// mix a relocation with a redesign. The base URLs point at this service.
builder.Services
    .AddHttpClient("moto-hub", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(1);
        client.BaseAddress = new Uri(
            builder.Configuration["MotoHubSettings:BaseUrl"]
                ?? throw new InvalidOperationException("MotoHubSettings:BaseUrl is not configured."));
    })
    .AddGatewayIdentityPropagation("projecty.rental-core", "service:rental-core");
builder.Services
    .AddHttpClient("rental-operations", client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration["RentalOperationsSettings:BaseUrl"]
                ?? throw new InvalidOperationException("RentalOperationsSettings:BaseUrl is not configured."));
    })
    .AddGatewayIdentityPropagation("projecty.rental-core");
builder.Services.AddScoped<RentalOperations.CrossCutting.Services.IMotorcycleService,
    RentalOperations.CrossCutting.Services.MotorcycleService>();
builder.Services.AddScoped<MotoHub.CrossCutting.IRentalOperationService, MotoHub.CrossCutting.RentalOperationService>();

builder.Services.AddScoped<RentalRepository>();
// Dual write only where the target engine is configured. Without it the service
// runs on Mongo alone, which is what every stack does until #135 finishes.
var targetSchema = builder.Configuration.GetConnectionString("TargetSchema");
if (string.IsNullOrWhiteSpace(targetSchema))
{
    builder.Services.AddScoped<IRentalRepository>(provider => provider.GetRequiredService<RentalRepository>());
}
else
{
    // One data source for the process. Each owns a connection pool, so building
    // one per write would leak a pool per write; the container disposes this one.
    builder.Services.AddSingleton(NpgsqlDataSource.Create(targetSchema));
    builder.Services.AddSingleton<RentalTargetWriter>();
    builder.Services.AddScoped<IRentalRepository>(provider => new DualWriteRentalRepository(
        provider.GetRequiredService<RentalRepository>(),
        provider.GetRequiredService<RentalTargetWriter>()));
    builder.Services.AddHostedService<RentalMirrorReconciler>();
}
builder.Services.AddScoped<IRentalService, RentalService>();

var app = builder.Build();

if (await DatabaseMigrationCommand.TryRunAsync<ApplicationDbContext>(args, app.Services))
{
    return;
}

if (SwaggerPolicy.IsEnabled(app.Environment, app.Configuration))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseProjectYIdempotency();

app.MapControllers();
app.MapProjectYHealthChecks();
app.MapOutboxMetrics<ApplicationDbContext>("rental-core");

app.Run();

public partial class Program { }
