using Npgsql;
using Testcontainers.PostgreSql;

namespace RentalOperationsTests.Integration.PostgreSql;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS rental_claims (
            id uuid PRIMARY KEY,
            motorcycle_licence_plate text NOT NULL,
            status text NOT NULL CHECK (status IN ('active', 'completed')),
            created_at timestamptz NOT NULL DEFAULT now()
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_rental_claims_one_active_per_motorcycle
            ON rental_claims (motorcycle_licence_plate)
            WHERE status = 'active';
        """;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("projecty_tests")
        .WithUsername("projecty")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var command = dataSource.CreateCommand(Schema);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ResetAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var command = dataSource.CreateCommand("TRUNCATE TABLE rental_claims;");
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
