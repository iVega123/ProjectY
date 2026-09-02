using Microsoft.EntityFrameworkCore;
using Minio;
using RabbitMQ.Client;
using RiderManager.Configurations;
using RiderManager.Data;
using Serilog.Formatting.Compact;
using Serilog;
using OpenTelemetry.Trace;
using Microsoft.OpenApi.Models;
using RiderManager.Services.RiderServices;
using RiderManager.Repositories;
using RiderManager.Services.RabbitMQService;
using RiderManager.Services.MinioStorageService;
using RiderManager.Managers;
using RiderManager.Services.PreSignedService;
using RiderManager.Services;
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

var minioConfig = builder.Configuration.GetSection("MinIO").Get<MinIOOptions>();
builder.Services.AddSingleton(minioConfig);
builder.Services.AddMinio(configureClient => configureClient
    .WithEndpoint(minioConfig.Endpoint, 9000)
    .WithCredentials(minioConfig.AccessKey, minioConfig.SecretKey)
    .WithSSL(false)
    .Build());

var rabbitMQConfig = builder.Configuration.GetSection("RabbitMQ").Get<RabbitMQOptions>();
builder.Services.AddSingleton<RabbitMQOptions>(rabbitMQConfig);
builder.Services.Configure<RabbitMQOptions>(builder.Configuration.GetSection("RabbitMQ"));
var postgresConnection = new NpgsqlConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Postgresql") ?? "Host=postgres;Port=5432");
builder.Services
    .AddProjectYHealthChecks()
    .AddTcpDependency("postgres", postgresConnection.Host ?? "postgres", postgresConnection.Port)
    .AddTcpDependency("rabbitmq", rabbitMQConfig?.HostName ?? "rabbitmq", 5672)
    .AddTcpDependency("minio", minioConfig?.Endpoint ?? "minio", 9000);
builder.Services.AddSingleton(new QueueMessageAuthenticator(
    builder.Configuration["Messaging:SigningKey"]
        ?? throw new InvalidOperationException("Messaging:SigningKey is not configured.")));
builder.Services.AddProjectYIdempotency(builder.Configuration, "rider-manager");


var serviceName = builder.Configuration["OTEL_SERVICE_NAME"]
    ?? builder.Configuration["ApplicationName"]
    ?? "rider-manager";

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

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgresql")));

builder.Services.AddGatewayIdentityAuthentication(
    builder.Configuration,
    "projecty.rider-manager");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddAutoMapper(_ => { }, typeof(Program));
builder.Services.AddScoped<IRiderService, RiderService>();
builder.Services.AddScoped<IRiderRepository, RiderRepository>();
builder.Services.AddSingleton<IRabbitMqService, RabbitMqService>();
builder.Services.AddSingleton<IMessagingConsumerService, MessagingConsumerService>();
builder.Services.AddSingleton<BoundedRabbitMqRetryRouter>();
builder.Services.AddHostedService<ConsumerHostedService>();
builder.Services.AddScoped<IRiderInboxProcessor, RiderInboxProcessor>();
builder.Services.AddScoped<RiderInboxMessageHandler>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Messaging:Inbox").Get<RiderInboxRetentionOptions>()
        ?? new RiderInboxRetentionOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<RiderInboxRetentionService>();
builder.Services.AddScoped<IMinioFileStorageService, MinioFileStorageService>();
builder.Services.AddScoped<IPresignedUrlService, PresignedUrlService>();
builder.Services.AddScoped<IRiderManager, RidersManager>();
builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<IdempotencyKeyOperationFilter>();
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "RiderManager", Version = "v1" });

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

app.Run();
