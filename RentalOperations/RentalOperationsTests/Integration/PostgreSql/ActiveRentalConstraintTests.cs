using Npgsql;

namespace RentalOperationsTests.Integration.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class ActiveRentalConstraintTests
{
    private readonly PostgreSqlFixture _database;

    public ActiveRentalConstraintTests(PostgreSqlFixture database)
    {
        _database = database;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentClaimsForSameMotorcycle_OneIsRejectedByDatabaseConstraint()
    {
        await _database.ResetAsync();

        const string licencePlate = "TEST-0001";
        using var ready = new CountdownEvent(2);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = new[]
        {
            TryCreateActiveRentalAsync(licencePlate, ready, start.Task),
            TryCreateActiveRentalAsync(licencePlate, ready, start.Task)
        };

        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "Both writes did not become ready in time.");
        start.SetResult(true);

        var outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes, outcome => outcome.Inserted);
        var rejected = Assert.Single(outcomes, outcome => !outcome.Inserted);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, rejected.SqlState);
        Assert.Equal("ux_rental_claims_one_active_per_motorcycle", rejected.ConstraintName);
        Assert.Equal(1, await CountActiveRentalsAsync(licencePlate));
    }

    private async Task<WriteOutcome> TryCreateActiveRentalAsync(
        string licencePlate,
        CountdownEvent ready,
        Task start)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        ready.Signal();
        await start;

        try
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO rental_claims (id, motorcycle_licence_plate, status)
                VALUES ($1, $2, 'active');
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue(licencePlate);

            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return WriteOutcome.Success;
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync();
            return new WriteOutcome(false, exception.SqlState, exception.ConstraintName);
        }
    }

    private async Task<long> CountActiveRentalsAsync(string licencePlate)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM rental_claims
            WHERE motorcycle_licence_plate = $1 AND status = 'active';
            """,
            connection);
        command.Parameters.AddWithValue(licencePlate);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record WriteOutcome(bool Inserted, string? SqlState, string? ConstraintName)
    {
        public static readonly WriteOutcome Success = new(true, null, null);
    }
}
