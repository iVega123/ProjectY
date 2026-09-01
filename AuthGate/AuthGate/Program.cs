using AuthGate.Data;
using AuthGate.Model;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;
using AuthGate.Configurations;
using AuthGate.Services.RabbitMQ;
using AuthGate.Services.File;
using AuthGate.Services;
using Npgsql;
using ProjectY.Shared.Health;
using ProjectY.Shared.Hosting;
using Serilog.Sinks.Elasticsearch;
using ProjectY.Shared.Messaging;
using ProjectY.Shared.Idempotency;
using AuthGate.Validators;

if (await HealthProbeCommand.TryRunAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton(new QueueMessageAuthenticator(
    builder.Configuration["Messaging:SigningKey"]
        ?? throw new InvalidOperationException("Messaging:SigningKey is not configured.")));
builder.Services.AddProjectYIdempotency(builder.Configuration, "auth-gate");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.OperationFilter<IdempotencyKeyOperationFilter>());

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgresql")));
builder.Services.AddScoped<IMessagingPublisherService, MessagingPublisherService>();
builder.Services.AddSingleton(new OutboxRelayOptions
{
    ServiceName = "auth-gate",
    HostName = rabbitMQConfig.HostName,
    VirtualHost = rabbitMQConfig.VirtualHost,
    UserName = rabbitMQConfig.UserName,
    Password = rabbitMQConfig.Password
});
builder.Services.AddSingleton<IOutboxTransport, RabbitMqOutboxTransport>();
builder.Services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
builder.Services.AddHostedService<OutboxRelay<ApplicationDbContext>>();
builder.Services.AddScoped<IFileValidationService, FileValidationService>();

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = true;
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedEmail = false;
})
.AddUserValidator<RiderUserDataAnnotationValidator>()
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    var jwtKey = builder.Configuration["Jwt:SigningKeys:AuthGate"] ?? throw new InvalidOperationException("Jwt:SigningKeys:AuthGate is not configured.");
    var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer is not configured.");
    var jwtAudience = builder.Configuration["Jwt:Audiences:AuthGate"] ?? throw new InvalidOperationException("Jwt:Audiences:AuthGate is not configured.");
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

var app = builder.Build();

if (await DatabaseMigrationCommand.TryRunAsync<ApplicationDbContext>(args, app.Services))
{
    return;
}

if (args.Contains("--bootstrap-admin", StringComparer.Ordinal))
{
    await AdminBootstrapper.BootstrapAsync(app.Services, app.Configuration, app.Logger);
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
app.MapOutboxMetrics<ApplicationDbContext>("auth-gate");

app.Run();

public partial class Program { }
