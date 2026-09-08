using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RentalOperations.Repository;
using RentalOperations.Services;

namespace RentalOperationsTests.Integration;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string GatewayIdentityKey = "test-only-gateway-identity-key-32-bytes";
    public const string GatewayIdentityAudience = "projecty.rental-core";

    public InMemoryRentalRepository Repository =>
        Services.GetRequiredService<InMemoryRentalRepository>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("GatewayIdentity:SigningKey", GatewayIdentityKey);
        builder.UseSetting("GatewayIdentity:SigningKeyId", "test-v1");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GatewayIdentity:SigningKey"] = GatewayIdentityKey,
                ["GatewayIdentity:SigningKeyId"] = "test-v1"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IRentalRepository>();
            services.AddSingleton<InMemoryRentalRepository>();
            services.AddSingleton<IRentalRepository>(provider =>
                provider.GetRequiredService<InMemoryRentalRepository>());
            // These tests exercise the authorization pipeline, not the projection.
            // The real store would pull MongoDbContext into a graph this factory
            // deliberately keeps in memory.
            services.RemoveAll<IRiderProjectionStore>();
            services.AddSingleton<IRiderProjectionStore>(new AlwaysVerifiedRiderProjection());
        });
    }

    public HttpClient CreateAuthenticatedClient(string role, string userId = "requesting-user")
    {
        var client = CreateDefaultClient(new TestGatewayIdentityHandler(role, userId));
        client.BaseAddress = new Uri("https://localhost");
        return client;
    }

    public HttpClient CreateUnauthenticatedClient()
    {
        var client = CreateDefaultClient();
        client.BaseAddress = new Uri("https://localhost");
        return client;
    }

}

internal sealed class AlwaysVerifiedRiderProjection : IRiderProjectionStore
{
    public Task<RiderView?> GetAsync(string riderId, CancellationToken token) =>
        Task.FromResult<RiderView?>(new RiderView(riderId, true, 1, "Ada Lovelace"));
}
