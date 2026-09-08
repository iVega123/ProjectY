using Npgsql;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using ProjectY.Shared.Health;
using ProjectY.Shared.Hosting;
using ProjectY.Shared.Idempotency;
using ProjectY.Shared.Observability;
using ProjectY.Shared.Security;
using RentalOperations.Configurations;
using RentalOperations.CrossCutting.Services;
using RentalOperations.Data;
using RentalOperations.Repository;
using RentalOperations.Services;
using RentalOperations.Services.RabbitMQService;
using Serilog;
using Serilog.Formatting.Compact;

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
    configureTracing: null,
    MongoTelemetry.ActivitySourceName);

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ApplicationName", serviceName)
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .WriteToProjectYTelemetry(builder.Configuration, serviceName)
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddProjectYIdempotency(builder.Configuration, "rental-operations");

var rabbitMQConfig = builder.Configuration.GetSection("RabbitMQ").Get<RabbitMQOptions>();
builder.Services.AddSingleton<RabbitMQOptions>(rabbitMQConfig);
builder.Services.Configure<RabbitMQOptions>(builder.Configuration.GetSection("RabbitMQ"));

var mongoDbSettings = builder.Configuration.GetSection("MongoDbSettings");
var mongoUrl = new MongoUrl(mongoDbSettings["ConnectionString"] ?? "mongodb://mongodb:27017");
builder.Services
    .AddProjectYHealthChecks()
    .AddTcpDependency("mongodb", mongoUrl.Server.Host, mongoUrl.Server.Port)
    .AddTcpDependency("rabbitmq", rabbitMQConfig?.HostName ?? "rabbitmq", 5672);
builder.Services.AddSingleton<MongoDbContext>(sp =>
    new MongoDbContext(mongoDbSettings["ConnectionString"], mongoDbSettings["DatabaseName"]));
builder.Services.AddHostedService<MongoRentalIndexInitializer>();
builder.Services.AddHostedService<RentalOperations.Services.RentalKafkaRelay>();
builder.Services.AddHostedService<RentalOperations.Services.PricingProjection>();
builder.Services.AddHostedService<RentalOperations.Services.RiderProjection>();
builder.Services.AddSingleton<RentalOperations.Services.IRiderProjectionStore, RentalOperations.Services.MongoRiderProjectionStore>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Messaging:Inbox").Get<MongoInboxOptions>()
        ?? new MongoInboxOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MongoInboxProcessor>();
builder.Services.AddHostedService<MongoInboxInitializer>();
builder.Services.AddGatewayIdentityAuthentication(
    builder.Configuration,
    "projecty.rental-operations");
builder.Services.AddControllers();
builder.Services.AddAutoMapper(_ => { }, typeof(Program));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<IdempotencyKeyOperationFilter>();
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "RentalOperations", Version = "v1" });

});

builder.Services
    .AddHttpClient("moto-hub", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(1);
        client.BaseAddress = new Uri(
            builder.Configuration["MotoHubSettings:BaseUrl"]
                ?? throw new InvalidOperationException(
                    "MotoHubSettings:BaseUrl is not configured."));
    })
    .AddGatewayIdentityPropagation(
        "projecty.moto-hub",
        "service:rental-operations");


builder.Services.AddSingleton<IRabbitMqService, RabbitMqService>();
builder.Services.AddSingleton<IMessagingConsumerService, MessagingConsumerService>();
builder.Services.AddHostedService<ConsumerHostedService>();

builder.Services.AddScoped<IMotorcycleService, MotorcycleService>();

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

public partial class Program { }
