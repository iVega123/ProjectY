using ProjectY.Shared.Security;
using System.Security.Claims;

namespace RentalOperationsTests.Integration;

internal sealed class TestGatewayIdentityHandler(string role, string userId) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Role, role)
        ], "TestGatewayIdentity"));
        new GatewayIdentitySigner(CustomWebApplicationFactory.GatewayIdentityKey, "test-v1")
            .Sign(request, principal, CustomWebApplicationFactory.GatewayIdentityAudience);
        return base.SendAsync(request, cancellationToken);
    }
}
