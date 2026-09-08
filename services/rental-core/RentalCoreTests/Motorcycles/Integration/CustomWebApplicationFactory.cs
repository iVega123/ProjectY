using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using MotoHub.Data;
using ProjectY.Shared.Security;
using RabbitMQ.Client;
using System.Security.Claims;

namespace MotoHubTests.Integration
{
    public class CustomWebApplicationFactory<TStartup> : WebApplicationFactory<TStartup> where TStartup : class
    {
        public const string GatewayIdentityKey = "test-only-gateway-identity-key-32-bytes";
        public const string GatewayIdentityAudience = "projecty.rental-core";

        // Nada aqui abre conexão: o contexto vai para memória e a aposentadoria
        // é substituída. A string existe só para o processo subir.
        private const string UnusedConnectionString = "Host=unused;Database=unused;Username=unused";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("GatewayIdentity:SigningKey", GatewayIdentityKey);
            builder.UseSetting("GatewayIdentity:SigningKeyId", "test-v1");
            builder.UseSetting("ConnectionStrings:Postgresql", UnusedConnectionString);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();

                // A aposentadoria é a única coisa aqui que fala SQL cru, e este
                // host roda sobre o provider em memória. As duas garantias que
                // ela carrega -- a corrida de retirada e a recusa com aluguel
                // ativo -- têm teste próprio contra o banco de verdade, em
                // Motorcycles/Integration/PostgreSql/MotorcycleRetirementTests.
                services.RemoveAll<MotoHub.Services.IMotorcycleRetirement>();
                services.AddSingleton<MotoHub.Services.IMotorcycleRetirement, AcceptingRetirement>();

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
                    {"GatewayIdentity:SigningKey", GatewayIdentityKey},
                    {"GatewayIdentity:SigningKeyId", "test-v1"},
                    {"ConnectionStrings:Postgresql", UnusedConnectionString},
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

        public new HttpClient CreateClient()
        {
            var client = CreateDefaultClient(new TestGatewayIdentityMarkerHandler());
            client.BaseAddress = new Uri("https://localhost");
            return client;
        }

        private sealed class AcceptingRetirement : MotoHub.Services.IMotorcycleRetirement
        {
            public Task<MotoHub.Services.MotorcycleRetirementResult> RetireAsync(
                Guid motorcycleId, DateTime retiredAtUtc, string reason, CancellationToken token = default) =>
                Task.FromResult(MotoHub.Services.MotorcycleRetirementResult.Retired);
        }

        private sealed class TestGatewayIdentityMarkerHandler : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.Headers.Authorization is { Scheme: "Bearer", Parameter: "valid-admin" })
                {
                    var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "test-user"),
                        new Claim(ClaimTypes.Role, "Admin")
                    ], "TestGatewayIdentity"));
                    new GatewayIdentitySigner(GatewayIdentityKey, "test-v1")
                        .Sign(request, principal, GatewayIdentityAudience);
                }

                request.Headers.Authorization = null;
                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
