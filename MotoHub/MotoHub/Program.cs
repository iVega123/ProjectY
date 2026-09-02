using Serilog.Formatting.Compact;
using Serilog;
using OpenTelemetry.Trace;
using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using MotoHub.Services;
using MotoHub.Repositories;
using Microsoft.OpenApi.Models;
using MotoHub.Configurations;
using MotoHub.Services.RabbitMQ;
using MotoHub.CrossCutting;
using Npgsql;
using ProjectY.Shared.Health;
using ProjectY.Shared.Hosting;
using ProjectY.Shared.Messaging;
using ProjectY.Shared.Idempotency;
using ProjectY.Shared.Observability;
using ProjectY.Shared.Security;

if (await HealthProbeCommand.TryRunAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);

var serviceName = builder.Configuration["OTEL_SERVICE_NAME"]
    ?? builder.Configuration["ApplicationName"]
    ?? "moto-hub";

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

var rabbitMQConfig = builder.Configuration.GetSection("RabbitMQ").Get<RabbitMQOptions>()
    ?? throw new InvalidOperationException("RabbitMQ configuration is missing.");
builder.Services.AddSingleton<RabbitMQOptions>(rabbitMQConfig);
builder.Services.AddProjectYIdempotency(builder.Configuration, "moto-hub");
var postgresConnection = new NpgsqlConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Postgresql") ?? "Host=postgres;Port=5432");
builder.Services
    .AddProjectYHealthChecks()
    .AddTcpDependency("postgres", postgresConnection.Host ?? "postgres", postgresConnection.Port)
    .AddTcpDependency("rabbitmq", rabbitMQConfig.HostName, 5672);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgresql")));

builder.Services.AddGatewayIdentityAuthentication(
    builder.Configuration,
    "projecty.moto-hub");

builder.Services.AddControllers();
builder.Services.AddAutoMapper(_ => { }, typeof(Program));
builder.Services
    .AddHttpClient("rental-operations", client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration["RentalOperationsSettings:BaseUrl"]
                ?? throw new InvalidOperationException(
                    "RentalOperationsSettings:BaseUrl is not configured."));
    })
    .AddGatewayIdentityPropagation("projecty.rental-operations");
builder.Services.AddScoped<IApplicationDbContext>(services =>
    services.GetRequiredService<ApplicationDbContext>());
builder.Services.AddScoped<IMotorcycleRepository, MotorcycleRepository>();
builder.Services.AddScoped<IMotorcycleService, MotorcycleService>();
builder.Services.AddScoped<IMessagingPublisherService, MessagingPublisherService>();
builder.Services.AddSingleton(new OutboxRelayOptions
{
    ServiceName = "moto-hub",
    HostName = rabbitMQConfig.HostName,
    VirtualHost = rabbitMQConfig.VirtualHost,
    UserName = rabbitMQConfig.UserName,
    Password = rabbitMQConfig.Password
});
builder.Services.AddSingleton<IOutboxTransport, RabbitMqOutboxTransport>();
builder.Services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
builder.Services.AddHostedService<OutboxRelay<ApplicationDbContext>>();
builder.Services.AddScoped<IRentalOperationService, RentalOperationService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<IdempotencyKeyOperationFilter>();
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "MotoHub", Version = "v1" });
});

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
app.MapOutboxMetrics<ApplicationDbContext>("moto-hub");

app.Run();

public partial class Program { }
