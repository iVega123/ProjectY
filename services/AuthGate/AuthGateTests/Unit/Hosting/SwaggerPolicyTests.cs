using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using ProjectY.Shared.Hosting;
using Xunit;

namespace AuthGateTests.Unit.Hosting;

public class SwaggerPolicyTests
{
    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Production", false, false)]
    public void IsEnabled_RequiresDevelopmentAndExplicitToggle(
        string environmentName,
        bool toggle,
        bool expected)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(value => value.EnvironmentName).Returns(environmentName);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Swagger:Enabled"] = toggle.ToString()
            })
            .Build();

        Assert.Equal(expected, SwaggerPolicy.IsEnabled(environment.Object, configuration));
    }
}
