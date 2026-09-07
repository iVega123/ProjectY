using Microsoft.AspNetCore.Authorization;
using RiderManager.Controllers;

namespace RiderManagerTests.Unit.Controllers;

public sealed class RiderAuthorizationMetadataTests
{
    [Fact]
    public void Controller_RequiresAuthenticatedGatewayIdentity()
    {
        var attribute = Assert.Single(
            typeof(RidersController).GetCustomAttributes(typeof(AuthorizeAttribute), true));

        Assert.Null(((AuthorizeAttribute)attribute).Roles);
    }

    [Theory]
    [InlineData(nameof(RidersController.GetAllRiders), "Admin")]
    [InlineData(nameof(RidersController.DeleteRider), "Admin")]
    [InlineData(nameof(RidersController.UpdateRiderCNH), "Rider")]
    public void RoleRestrictedActions_DeclareDomainRole(string actionName, string expectedRole)
    {
        var action = typeof(RidersController).GetMethod(actionName);
        Assert.NotNull(action);

        var attribute = Assert.Single(
            action.GetCustomAttributes(typeof(AuthorizeAttribute), true));

        Assert.Equal(expectedRole, ((AuthorizeAttribute)attribute).Roles);
    }
}
