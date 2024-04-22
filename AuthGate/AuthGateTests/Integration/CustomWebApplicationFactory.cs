using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using AuthGate.Data;
using Moq;
using RabbitMQ.Client;

namespace AuthGateTests.Integration
{
    public class CustomWebApplicationFactory<TStartup> : WebApplicationFactory<TStartup> where TStartup : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
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
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

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
                    {"JwtKey", "pnXhunyWll1LgERT86wXwMH5I6ieQC2M"}
                };

                configBuilder.Sources.Clear();
                configBuilder.AddInMemoryCollection(integrationTestConfig);
            });
        }
    }
}
