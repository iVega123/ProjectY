using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using AuthGate.Data;
using AuthGate.Model;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Moq;
using RabbitMQ.Client;
using Xunit;

namespace AuthGateTests.Integration
{
    public class CustomWebApplicationFactory<TStartup> : WebApplicationFactory<TStartup> where TStartup : class
    {
        public async Task CreateAdminAsync(string email, string password)
        {
            using var scope = Services.CreateScope();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (!await roleManager.RoleExistsAsync("Admin"))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole("Admin"));
                Assert.True(roleResult.Succeeded);
            }

            var user = new AdminUser { UserName = email, Email = email };
            var createResult = await userManager.CreateAsync(user, password);
            Assert.True(createResult.Succeeded);

            var assignmentResult = await userManager.AddToRoleAsync(user, "Admin");
            Assert.True(assignmentResult.Succeeded);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Messaging:SigningKey", "test-only-queue-signing-key-with-32-bytes");

            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();

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
                    {"Jwt:Issuer", "projecty.auth-gate"},
                    {"Jwt:Audiences:AuthGate", "projecty.auth-gate"},
                    {"Jwt:SigningKeys:AuthGate", "test-only-auth-gate-key-with-32-bytes"},
                    {"Jwt:Audiences:MotoHub", "projecty.moto-hub"},
                    {"Jwt:SigningKeys:MotoHub", "test-only-moto-hub-signing-key-0001"},
                    {"Messaging:SigningKey", "test-only-queue-signing-key-with-32-bytes"}
                };

                configBuilder.Sources.Clear();
                configBuilder.AddInMemoryCollection(integrationTestConfig);
            });
        }
    }
}
