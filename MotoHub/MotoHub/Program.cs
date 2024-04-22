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
using RabbitMQ.Client;
using Serilog.Sinks.Elasticsearch;
using MotoHub.Configurations;
using MotoHub.Services.RabbitMQ;
using MotoHub.CrossCutting;

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

var rabbitMQConfig = builder.Configuration.GetSection("RabbitMQ").Get<RabbitMQOptions>();
builder.Services.AddSingleton<RabbitMQOptions>(rabbitMQConfig);

builder.Services.AddSingleton<IConnection>(sp =>
{
    var rabbitMQOptions = sp.GetRequiredService<RabbitMQOptions>();
    var factory = new ConnectionFactory()
    {
        HostName = rabbitMQOptions.HostName,
        UserName = rabbitMQOptions.UserName,
        Password = rabbitMQOptions.Password
    };
    return factory.CreateConnection();
});

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgresql")));

var jwtKey = builder.Configuration["JwtKey"] ?? throw new InvalidOperationException("JwtKey is not set in the environment variables.");
var keyBytes = Encoding.UTF8.GetBytes(jwtKey);
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
        ValidateIssuer = false,
        ValidateAudience = false
    };
});

builder.Services.AddControllers();
builder.Services.AddAutoMapper(typeof(Program));
builder.Services.AddHttpClient();
builder.Services.AddScoped<IApplicationDbContext, ApplicationDbContext>();
builder.Services.AddScoped<IMotorcycleRepository ,MotorcycleRepository>();
builder.Services.AddScoped<AdminAuthorizationFilter>();
builder.Services.AddScoped<IMotorcycleService, MotorcycleService>();
builder.Services.AddScoped<IMessagingPublisherService, MessagingPublisherService>();
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }