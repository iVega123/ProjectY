using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using ProjectY.Shared.Health;
using ProjectY.Shared.Hosting;
using ProjectY.Shared.Idempotency;
using ProjectY.Shared.Security;
using RentalOperations.Configurations;
using RentalOperations.CrossCutting.Services;
using RentalOperations.Data;
using RentalOperations.Repository;
using RentalOperations.Services;
using RentalOperations.Services.RabbitMQService;
using Serilog;

if (await HealthProbeCommand.TryRunAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);

var applicationName = builder.Configuration["ApplicationName"];

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ApplicationName", applicationName)
    .WriteTo.Console()
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
    .AddHttpClient("rider-manager", client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration["RiderManagerSettings:BaseUrl"]
                ?? throw new InvalidOperationException(
                    "RiderManagerSettings:BaseUrl is not configured."));
    })
    .AddGatewayIdentityPropagation("projecty.rider-manager");
builder.Services
    .AddHttpClient("moto-hub", client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration["MotoHubSettings:BaseUrl"]
                ?? throw new InvalidOperationException(
                    "MotoHubSettings:BaseUrl is not configured."));
    })
    .AddGatewayIdentityPropagation(
        "projecty.moto-hub",
        "service:rental-operations");

builder.Services.AddScoped<IRiderManagerService, RiderManagerService>();

builder.Services.AddSingleton<IRabbitMqService, RabbitMqService>();
builder.Services.AddSingleton<IMessagingConsumerService, MessagingConsumerService>();
builder.Services.AddHostedService<ConsumerHostedService>();

builder.Services.AddScoped<IMotorcycleService, MotorcycleService>();

builder.Services.AddScoped<IRentalRepository, RentalRepository>();
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
