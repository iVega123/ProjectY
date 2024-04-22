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
using Serilog.Sinks.Elasticsearch;

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
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddAutoMapper(typeof(Program));
builder.Services.AddScoped<AdminAuthorizationFilter>();
builder.Services.AddScoped<AuthorizationFilter>();
builder.Services.AddScoped<IRiderService, RiderService>();
builder.Services.AddScoped<IRiderRepository, RiderRepository>();
builder.Services.AddSingleton<IRabbitMqService, RabbitMqService>();
builder.Services.AddSingleton<IMessagingConsumerService, MessagingConsumerService>();
builder.Services.AddHostedService<ConsumerHostedService>();
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
