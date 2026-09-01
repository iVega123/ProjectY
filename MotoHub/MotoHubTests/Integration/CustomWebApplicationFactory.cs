using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using MotoHub.CrossCutting;
using MotoHub.Data;
using RabbitMQ.Client;

namespace MotoHubTests.Integration
{
    public class CustomWebApplicationFactory<TStartup> : WebApplicationFactory<TStartup> where TStartup : class
    {
        public const string JwtKey = "test-only-moto-hub-key-with-32-bytes";
        public const string JwtIssuer = "projecty.auth-gate";
        public const string JwtAudience = "projecty.moto-hub";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();

                var serviceDescriptor = services.FirstOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IRentalOperationService));

                if (serviceDescriptor != null)
                {
                    services.Remove(serviceDescriptor);
                }

                var mockService = new Mock<IRentalOperationService>();
                mockService.Setup(service => service.GetRentalsByMotorcycleLicencePlateAsync("mock"))
                           .ReturnsAsync(true);
                mockService.Setup(service => service.TryRetireMotorcycleAsync(It.IsAny<string>()))
                           .ReturnsAsync(true);

                services.AddSingleton<IRentalOperationService>(mockService.Object);

                var modelMock = new Mock<IModel>();
                modelMock.Setup(m => m.BasicPublish(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<IBasicProperties>(),
                    It.IsAny<ReadOnlyMemory<byte>>()));

                var connectionMock = new Mock<IConnection>();
                connectionMock.Setup(conn => conn.CreateModel()).Returns(modelMock.Object);

                var descriptors = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IConnection));
                if (descriptors != null)
                {
                    services.Remove(descriptors);
                }

                services.AddSingleton<IConnection>(connectionMock.Object);
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();

                services.AddDbContext<ApplicationDbContext>(options =>
                {
                    options.UseInMemoryDatabase("TestingDB");
                });
            });

            builder.ConfigureAppConfiguration((context, configBuilder) =>
            {
                context.HostingEnvironment.EnvironmentName = "Testing";

                var integrationTestConfig = new Dictionary<string, string>
                {
                    {"Jwt:SigningKey", JwtKey},
                    {"Jwt:Issuer", JwtIssuer},
                    {"Jwt:Audience", JwtAudience},
                    {"MotoHubApiKey", "" },
                    {"RabbitMQ:HostName", "unused"},
                    {"RabbitMQ:VirtualHost", "unused"},
                    {"RabbitMQ:UserName", "unused"},
                    {"RabbitMQ:Password", "unused"},
                    {"RabbitMQ:LicenceUpdateQueueName", "licence-update-test"}
                };

                configBuilder.Sources.Clear();
                configBuilder.AddInMemoryCollection(integrationTestConfig);
            });
        }
    }
}
