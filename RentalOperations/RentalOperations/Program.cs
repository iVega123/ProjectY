using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using ProjectY.Shared.Health;
using ProjectY.Shared.Hosting;
using ProjectY.Shared.Idempotency;
using RentalOperations.Configurations;
using RentalOperations.CrossCutting.Services;
using RentalOperations.Data;
using RentalOperations.Filters;
using RentalOperations.Repository;
using RentalOperations.Services;
using RentalOperations.Services.RabbitMQService;
using Serilog;
using Serilog.Sinks.Elasticsearch;
using System.Text;

if (await HealthProbeCommand.TryRunAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);

var applicationName = builder.Configuration["ApplicationName"];

var elasticUrl = builder.Configuration["ElasticSearchURL"];

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ApplicationName", applicationName)
    .WriteTo.Console()
    .WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(elasticUrl))
    {
        AutoRegisterTemplate = true,
        AutoRegisterTemplateVersion = AutoRegisterTemplateVersion.ESv7,
        IndexFormat = $"{applicationName.ToLower()}-logs-{DateTime.UtcNow:yyyy.MM}"
    })
    .CreateLogger();

builder.Services.Configure<RiderManagerSettings>(builder.Configuration.GetSection("RiderManagerSettings"));
builder.Services.Configure<MotoHubSettings>(builder.Configuration.GetSection("MotoHubSettings"));
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
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    var jwtKey = builder.Configuration["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");
    var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer is not configured.");
    var jwtAudience = builder.Configuration["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt:Audience is not configured.");
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience
    };
});
builder.Services.AddControllers();
builder.Services.AddAutoMapper(_ => { }, typeof(Program));
builder.Services.AddScoped<AuthorizationFilter>();
builder.Services.AddScoped<AdminAuthorizationFilter>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<IdempotencyKeyOperationFilter>();
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "RentalOperations", Version = "v1" });

    // Configuração do esquema de segurança JWT no Swagger
    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "JWT Authentication",
        Description = "Enter JWT Bearer token **_only_**",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new OpenApiReference
        {
            Id = JwtBearerDefaults.AuthenticationScheme,
            Type = ReferenceType.SecurityScheme
        }
    };
    c.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, securityScheme);

    var securityRequirement = new OpenApiSecurityRequirement
    {
        { securityScheme, new[] { "Bearer" } }
    };
    c.AddSecurityRequirement(securityRequirement);
});

builder.Services.AddHttpClient();

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
