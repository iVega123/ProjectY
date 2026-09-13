using Npgsql;

namespace RentalOperations.Services;

/// <summary>
/// Bounds every database wait below the gateway's per-attempt budget.
///
/// The gateway gives a rental request 2.5 s per attempt. Npgsql's defaults are a
/// 15 s connect timeout and a 30 s command timeout, so with the database down this
/// service never answered: the gateway timed out, retried the POST with its
/// Idempotency-Key, and the retry met the first attempt's pending claim -- a 409
/// "request already in progress" after 2.5 s, which the breaker counts as a
/// success and never opens on. With these deadlines the service answers first,
/// with its own 503 and Retry-After, and the breaker sees what happened.
///
/// Cancellation is not waited for: a database that does not answer a query will
/// not answer the cancellation of it either, and waiting spends the budget twice.
/// </summary>
public static class DatabaseDeadlines
{
    public const int ConnectSeconds = 2;
    public const int CommandSeconds = 2;

    public static string Apply(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.Timeout = Bound(builder.Timeout, ConnectSeconds);
        builder.CommandTimeout = Bound(builder.CommandTimeout, CommandSeconds);
        builder.CancellationTimeout = -1;
        return builder.ConnectionString;
    }

    // Zero means "wait forever" to Npgsql; an explicit smaller value is kept.
    private static int Bound(int configured, int ceiling) =>
        configured <= 0 ? ceiling : Math.Min(configured, ceiling);
}
