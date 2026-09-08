using Npgsql;
using Testcontainers.PostgreSql;

namespace RentalCoreTests.Integration;

/// <summary>
/// Um banco de verdade, com o schema que vai para produção.
///
/// O schema não é redigitado aqui: os arquivos de deploy/db/sql são copiados
/// para a saída da compilação e aplicados como estão. Um teste que monta o
/// próprio schema prova que o código concorda consigo mesmo; este prova que o
/// código concorda com o que o cockroach-init aplica -- que é a pergunta que
/// interessa quando a garantia central do sistema é um índice do banco.
///
/// O engine é PostgreSQL e não CockroachDB porque o job de portabilidade
/// (.github/workflows/schema-portability.yml) já aplica estes mesmos arquivos
/// nos dois e afirma a invariante em cada um. Aqui o que se testa é o serviço.
///
/// Um contêiner para toda a coleção, e não um por classe: containers concorrentes
/// já derrubaram esta suíte no CI, e cada classe limpa as tabelas de que precisa.
/// </summary>
public sealed class RentalCoreDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("projecty")
        .WithUsername("projecty")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        foreach (var file in SchemaFiles())
        {
            await using var command = dataSource.CreateCommand(await File.ReadAllTextAsync(file));
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Os arquivos numerados, em ordem, com o bootstrap do PostgreSQL na frente.</summary>
    private static IEnumerable<string> SchemaFiles()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "db");
        yield return Path.Combine(directory, "000_bootstrap.postgres.sql");
        foreach (var file in Directory.EnumerateFiles(directory, "0*_*.sql")
                     .Where(path => !Path.GetFileName(path).StartsWith("000_", StringComparison.Ordinal))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            yield return file;
        }
    }

    public async Task ResetAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var command = dataSource.CreateCommand("""
            TRUNCATE TABLE rentals, motorcycles, outbox, inbox,
                           rider_projection, projection_snapshots, "OutboxMessages"
            RESTART IDENTITY CASCADE
            """);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Insere uma moto e devolve o id, que é o que os aluguéis referenciam.</summary>
    public async Task<Guid> AddMotorcycleAsync(string licensePlate, DateTime? retiredAtUtc = null)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO motorcycles (license_plate, model, year, retired_at)
            VALUES (@plate, 'Test model', 2026, @retired)
            RETURNING id
            """);
        command.Parameters.AddWithValue("plate", licensePlate);
        command.Parameters.AddWithValue("retired", retiredAtUtc is { } retired
            ? DateTime.SpecifyKind(retired, DateTimeKind.Utc)
            : DBNull.Value);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class RentalCoreDatabaseCollection : ICollectionFixture<RentalCoreDatabase>
{
    public const string Name = "rental-core database";
}
