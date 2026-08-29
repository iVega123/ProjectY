using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using RentalOperations.Filters;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace RentalOperationsTests.Filters;

public class AdminAuthorizationFilterTests
{
    private const string JwtKey = "test-only-key-with-at-least-32-bytes";
    private const string ApiKey = "test-api-key";

    [Fact]
    public void RiderToken_ReturnsForbidden()
    {
        var context = CreateContext(CreateToken("Rider"));
        var filter = CreateFilter();

        filter.OnAuthorization(context);

        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public void AdminToken_IsAuthorized()
    {
        var context = CreateContext(CreateToken("Admin"));
        var filter = CreateFilter();

        filter.OnAuthorization(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void InvalidToken_ReturnsUnauthorized()
    {
        var context = CreateContext("not-a-jwt");
        var filter = CreateFilter();

        filter.OnAuthorization(context);

        Assert.IsType<UnauthorizedResult>(context.Result);
    }

    private static AdminAuthorizationFilter CreateFilter()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtKey"] = JwtKey,
                ["RentalOperationsApiKey"] = ApiKey
            })
            .Build();

        return new AdminAuthorizationFilter(configuration);
    }

    private static AuthorizationFilterContext CreateContext(string token)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = $"Bearer {token}";

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        return new AuthorizationFilterContext(actionContext, []);
    }

    private static string CreateToken(string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            claims: [new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
