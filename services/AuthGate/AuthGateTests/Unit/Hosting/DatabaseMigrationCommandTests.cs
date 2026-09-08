using Microsoft.Extensions.Configuration;
using ProjectY.Shared.Hosting;
using Xunit;

namespace AuthGateTests.Unit.Hosting;

public sealed class DatabaseMigrationCommandTests
{
    [Fact]
    public void MigrationArgument_IsValidCommandLineConfigurationAndIsRecognized()
    {
        var configuration = new ConfigurationBuilder()
            .AddCommandLine([DatabaseMigrationCommand.Argument])
            .Build();

        Assert.Equal("true", configuration["migrate"]);
        Assert.True(DatabaseMigrationCommand.IsRequested([DatabaseMigrationCommand.Argument]));
    }

    [Fact]
    public void BareMigrationSwitch_IsNotRecognized()
    {
        Assert.False(DatabaseMigrationCommand.IsRequested(["--migrate"]));
    }
}
