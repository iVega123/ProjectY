using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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

var rabbitMQConfig = builder.Configuration.GetSection("RabbitMQ").Get<RabbitMQOptions>();
builder.Services.AddSingleton<RabbitMQOptions>(rabbitMQConfig);
builder.Services.Configure<RabbitMQOptions>(builder.Configuration.GetSection("RabbitMQ"));

var mongoDbSettings = builder.Configuration.GetSection("MongoDbSettings");
builder.Services.AddSingleton<MongoDbContext>(sp =>
    new MongoDbContext(mongoDbSettings["ConnectionString"], mongoDbSettings["DatabaseName"]));
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
builder.Services.AddScoped<AuthorizationFilter>();
builder.Services.AddScoped<AdminAuthorizationFilter>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
