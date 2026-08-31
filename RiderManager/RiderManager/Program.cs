using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Minio;
using RabbitMQ.Client;
using RiderManager.Configurations;
using RiderManager.Data;
using Serilog.Formatting.Compact;
using Serilog;
using System.Text;
using Microsoft.OpenApi.Models;
using RiderManager.Filters;
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
using Serilog.Sinks.Elasticsearch;
using ProjectY.Shared.Messaging;

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

builder.Host.UseSerilog();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgresql")));

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
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddAutoMapper(_ => { }, typeof(Program));
builder.Services.AddScoped<AdminAuthorizationFilter>();
builder.Services.AddScoped<AuthorizationFilter>();
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
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "RiderManager", Version = "v1" });

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

app.MapControllers();
app.MapProjectYHealthChecks();

app.Run();
