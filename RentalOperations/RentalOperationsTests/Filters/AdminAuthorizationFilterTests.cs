using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using RentalOperations.Filters;
using System.Security.Claims;

namespace RentalOperationsTests.Filters;

public class AdminAuthorizationFilterTests
{
    private const string ApiKey = "test-api-key";

    [Fact]
    public void RiderToken_ReturnsForbidden()
    {
        var context = CreateContext("Rider");
        var filter = CreateFilter();

        filter.OnAuthorization(context);

        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public void AdminToken_IsAuthorized()
    {
        var context = CreateContext("Admin");
        var filter = CreateFilter();

        filter.OnAuthorization(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void UnauthenticatedRequest_ReturnsUnauthorized()
    {
        var context = CreateContext();
        var filter = CreateFilter();

        filter.OnAuthorization(context);

        Assert.IsType<UnauthorizedResult>(context.Result);
    }

    private static AdminAuthorizationFilter CreateFilter()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RentalOperationsApiKey"] = ApiKey
            })
            .Build();

        return new AdminAuthorizationFilter(configuration);
    }

    private static AuthorizationFilterContext CreateContext(string? role = null)
    {
        var httpContext = new DefaultHttpContext();
        if (role is not null)
        {
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.Role, role)],
                    authenticationType: "Test"));
        }

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        return new AuthorizationFilterContext(actionContext, []);
    }
}
