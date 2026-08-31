using Serilog.Formatting.Compact;
using Serilog;
using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MotoHub.Services;
using MotoHub.Repositories;
using Microsoft.OpenApi.Models;
using MotoHub.Filters;
using Serilog.Sinks.Elasticsearch;
using MotoHub.Configurations;
using MotoHub.Services.RabbitMQ;
using MotoHub.CrossCutting;
using Npgsql;
using ProjectY.Shared.Health;
using ProjectY.Shared.Hosting;
using ProjectY.Shared.Messaging;

if (await HealthProbeCommand.TryRunAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RentalOperationsSettings>(builder.Configuration.GetSection("RentalOperationsSettings"));

var isTesting = builder.Environment.IsEnvironment("Testing");

var applicationName = builder.Configuration["ApplicationName"];
var elasticUrl = builder.Configuration["ElasticSearchURL"];

var loggerConfig = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ApplicationName", applicationName)
    .WriteTo.Console();

if (!isTesting)
{
    loggerConfig.WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(elasticUrl))
    {
        AutoRegisterTemplate = true,
        AutoRegisterTemplateVersion = AutoRegisterTemplateVersion.ESv7,
        IndexFormat = $"{applicationName.ToLower()}-logs-{DateTime.UtcNow:yyyy.MM}"
    });
}

Log.Logger = loggerConfig.CreateLogger();
builder.Host.UseSerilog();

var rabbitMQConfig = builder.Configuration.GetSection("RabbitMQ").Get<RabbitMQOptions>()
    ?? throw new InvalidOperationException("RabbitMQ configuration is missing.");
builder.Services.AddSingleton<RabbitMQOptions>(rabbitMQConfig);
var postgresConnection = new NpgsqlConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Postgresql") ?? "Host=postgres;Port=5432");
builder.Services
    .AddProjectYHealthChecks()
    .AddTcpDependency("postgres", postgresConnection.Host ?? "postgres", postgresConnection.Port)
    .AddTcpDependency("rabbitmq", rabbitMQConfig.HostName, 5672);

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
builder.Services.AddAutoMapper(_ => { }, typeof(Program));
builder.Services.AddHttpClient();
builder.Services.AddScoped<IApplicationDbContext>(services =>
    services.GetRequiredService<ApplicationDbContext>());
builder.Services.AddScoped<IMotorcycleRepository, MotorcycleRepository>();
builder.Services.AddScoped<AdminAuthorizationFilter>();
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
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "MotoHub", Version = "v1" });
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
app.MapOutboxMetrics<ApplicationDbContext>("moto-hub");

app.Run();

public partial class Program { }
