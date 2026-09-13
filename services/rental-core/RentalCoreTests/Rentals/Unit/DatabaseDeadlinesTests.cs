using System.Diagnostics;
using Npgsql;
using RentalOperations.Services;

namespace RentalOperationsTests.Unit;

/// <summary>
/// The fast half of the primary-database row. The connection string here is the
/// one the deployment ships -- no timeouts in it -- so removing
/// <see cref="DatabaseDeadlines"/> brings back Npgsql's 15 s and 30 s defaults
/// and both tests fail.
/// </summary>
public sealed class DatabaseDeadlinesTests
{
    private const string Deployed = "Host=cockroachdb;Port=26257;Database=projecty;Username=root;SSL Mode=Disable";

    [Fact]
    [Trait("Degradation", "database")]
    public void TheDeployedConnectionString_IsBoundedBelowTheGatewayAttemptBudget()
    {
        var bounded = new NpgsqlConnectionStringBuilder(DatabaseDeadlines.Apply(Deployed));

        // GATEWAY_UPSTREAM_RENTAL_OPERATIONS_TIMEOUT_MS is 2500.
        Assert.True(bounded.Timeout * 1000 < 2500, $"connect timeout {bounded.Timeout}s");
        Assert.True(bounded.CommandTimeout * 1000 < 2500, $"command timeout {bounded.CommandTimeout}s");
        Assert.Equal(-1, bounded.CancellationTimeout);
    }

    [Fact]
    public void AnExplicitShorterDeadline_IsKept()
    {
        var bounded = new NpgsqlConnectionStringBuilder(DatabaseDeadlines.Apply(Deployed + ";Timeout=1;Command Timeout=1"));

        Assert.Equal(1, bounded.Timeout);
        Assert.Equal(1, bounded.CommandTimeout);
    }

    [Fact]
    [Trait("Degradation", "database")]
    public async Task AnUnreachableDatabase_IsRefusedWithinTheAttemptBudget()
    {
        // A routable address that drops packets: the connect waits for the deadline
        // instead of failing at once, which is what a partitioned database does.
        var unreachable = "Host=10.255.255.1;Port=26257;Database=projecty;Username=root;SSL Mode=Disable";
        await using var dataSource = NpgsqlDataSource.Create(DatabaseDeadlines.Apply(unreachable));
        var timer = Stopwatch.StartNew();

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync();
        });

        Assert.NotNull(error);
        Assert.True(DependencyFailure.IsUnavailable(error!));
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(2500), $"refused after {timer.Elapsed}");
    }
}
