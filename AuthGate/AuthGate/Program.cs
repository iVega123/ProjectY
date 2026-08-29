using AuthGate.Data;
using AuthGate.Model;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;
using AuthGate.Configurations;
using RabbitMQ.Client;
using AuthGate.Services.RabbitMQ;
using AuthGate.Services.File;
using AuthGate.Services;
using Serilog.Sinks.Elasticsearch;
using ProjectY.Shared.Messaging;

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

var rabbitMQConfig = builder.Configuration.GetSection("RabbitMQ").Get<RabbitMQOptions>();
builder.Services.AddSingleton<RabbitMQOptions>(rabbitMQConfig);
builder.Services.AddSingleton(new QueueMessageAuthenticator(
    builder.Configuration["Messaging:SigningKey"]
        ?? throw new InvalidOperationException("Messaging:SigningKey is not configured.")));

builder.Services.AddSingleton<IConnection>(sp =>
{
    var rabbitMQOptions = sp.GetRequiredService<RabbitMQOptions>();
    var factory = new ConnectionFactory()
    {
        HostName = rabbitMQOptions.HostName,
        VirtualHost = rabbitMQOptions.VirtualHost,
        UserName = rabbitMQOptions.UserName,
        Password = rabbitMQOptions.Password
    };
    return factory.CreateConnection();
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgresql")));
builder.Services.AddScoped<IMessagingPublisherService, MessagingPublisherService>();
builder.Services.AddScoped<IFileValidationService, FileValidationService>();

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = true;
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedEmail = false;
})
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

if (args.Contains("--bootstrap-admin", StringComparer.Ordinal))
{
    await AdminBootstrapper.BootstrapAsync(app.Services, app.Configuration, app.Logger);
    return;
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }
