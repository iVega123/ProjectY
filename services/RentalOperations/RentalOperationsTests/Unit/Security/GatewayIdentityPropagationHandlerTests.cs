using Microsoft.AspNetCore.Http;
using ProjectY.Shared.Security;
using RentalOperationsTests.Integration;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;

namespace RentalOperationsTests.Unit.Security;

public sealed class GatewayIdentityPropagationHandlerTests
{
    [Fact]
    public async Task OutgoingServiceCall_CarriesSignedCurrentIdentity()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "rider-123"),
                new Claim(ClaimTypes.Role, "Rider")
            ], GatewayIdentityDefaults.AuthenticationScheme))
        };
        var capture = new CaptureHandler();
        var handler = new GatewayIdentityPropagationHandler(
            new HttpContextAccessor { HttpContext = context },
            new GatewayIdentitySigner(
                CustomWebApplicationFactory.GatewayIdentityKey,
                "test-v1"),
            "projecty.moto-hub",
            CreateServicePrincipal())
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("http://moto-hub/api/Motorcycles/ABC1234");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(capture.Request);
        Assert.Equal("rider-123", Header(capture.Request, GatewayIdentityDefaults.SubjectHeader));
        Assert.Equal("Rider", Header(capture.Request, GatewayIdentityDefaults.RolesHeader));
        Assert.Equal("test-v1", Header(capture.Request, GatewayIdentityDefaults.KeyIdHeader));
        Assert.StartsWith("v1=", Header(capture.Request, GatewayIdentityDefaults.SignatureHeader));
        Assert.False(capture.Request.Headers.Contains("X-API-Key"));
    }

    [Fact]
    public async Task OutgoingServiceCall_WithoutAuthenticatedIdentity_IsRejected()
    {
        var handler = new GatewayIdentityPropagationHandler(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new GatewayIdentitySigner(
                CustomWebApplicationFactory.GatewayIdentityKey,
                "test-v1"),
            "projecty.moto-hub",
            CreateServicePrincipal())
        {
            InnerHandler = new CaptureHandler()
        };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetAsync("http://moto-hub/api/Motorcycles/ABC1234"));
    }

    [Fact]
    public async Task BackgroundServiceCall_WithoutHttpContext_CarriesConfiguredServiceIdentity()
    {
        var capture = new CaptureHandler();
        var handler = new GatewayIdentityPropagationHandler(
            new HttpContextAccessor(),
            new GatewayIdentitySigner(
                CustomWebApplicationFactory.GatewayIdentityKey,
                "test-v1"),
            "projecty.moto-hub",
            CreateServicePrincipal())
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler);

        var response = await client.PostAsync(
            "http://moto-hub/api/Motorcycles/historical-references",
            JsonContent.Create(new { LicensePlates = new[] { "ABC1234" } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(capture.Request);
        Assert.Equal(
            "service:rental-operations",
            Header(capture.Request, GatewayIdentityDefaults.SubjectHeader));
        Assert.Equal(string.Empty, Header(capture.Request, GatewayIdentityDefaults.RolesHeader));
        Assert.StartsWith("v1=", Header(capture.Request, GatewayIdentityDefaults.SignatureHeader));
    }

    private static string Header(HttpRequestMessage request, string name) =>
        Assert.Single(request.Headers.GetValues(name));

    private static ClaimsPrincipal CreateServicePrincipal() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "service:rental-operations"),
            new Claim(ClaimTypes.Name, "service:rental-operations")
        ], GatewayIdentityDefaults.AuthenticationScheme));

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
