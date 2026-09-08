using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using MotoHub.Models;
using MotoHub.Services;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MotoHubTests.Integration.PostgreSql;

/// <summary>
/// The target schema is applied here from deploy/db/sql/001_schema.sql itself, not
/// from a copy: a projector that agrees with a transcription of the schema and
/// disagrees with the schema is exactly the failure this has to catch. The engine
/// is PostgreSQL rather than CockroachDB because schema-portability.yml already
/// proves the same file produces the same schema on both.
/// </summary>
public sealed class MotorcycleProjectorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _source = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("moto_hub_source").WithUsername("projecty").Build();
    private readonly PostgreSqlContainer _target = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("projecty").WithUsername("projecty").Build();
    private ApplicationDbContext _context = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_source.StartAsync(), _target.StartAsync());
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_source.GetConnectionString()).Options);
        await _context.Database.MigrateAsync();

        var schema = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "target-schema", "001_schema.sql"));
        // The GRANTs name roles that 000_bootstrap creates; the projector does not
        // exercise them and this container has no such roles.
        schema = string.Join('\n', schema.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("GRANT", StringComparison.Ordinal)));
        await using var connection = new NpgsqlConnection(_target.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(schema, connection);
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() =>
        Task.WhenAll(_source.DisposeAsync().AsTask(), _target.DisposeAsync().AsTask());

    private async Task<List<(Guid Id, string Plate, DateTime? Retired)>> TargetRows()
    {
        await using var connection = new NpgsqlConnection(_target.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, license_plate, retired_at FROM motorcycles ORDER BY license_plate", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(Guid, string, DateTime?)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2)));
        return rows;
    }

    private Motorcycle Add(string plate, int year = 2026, string? id = null)
    {
        var motorcycle = new Motorcycle
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Year = year,
            Model = "Projection proof",
            LicensePlate = plate,
            RegistrationDate = DateTime.UtcNow
        };
        _context.Motorcycles.Add(motorcycle);
        return motorcycle;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProjectionFillsTheTableRentalsWillReference()
    {
        Add("PRJ-0001");
        Add("PRJ-0002");
        await _context.SaveChangesAsync();

        var result = await MotorcycleProjector.ProjectAsync(_context, _target.GetConnectionString(), default);

        Assert.Equal(2, result.Applied);
        Assert.Empty(result.Rejected);
        Assert.Equal(["PRJ-0001", "PRJ-0002"], (await TargetRows()).Select(row => row.Plate));
    }

    // The projector runs every pass, so it re-reads rows it has already written.
    // If that were an insert rather than an upsert, the second pass would fail on
    // the primary key and the projection would never converge.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RepeatedPassesConvergeInsteadOfConflicting()
    {
        var motorcycle = Add("PRJ-0003");
        await _context.SaveChangesAsync();

        await MotorcycleProjector.ProjectAsync(_context, _target.GetConnectionString(), default);
        motorcycle.RetiredAtUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        await _context.SaveChangesAsync();
        var second = await MotorcycleProjector.ProjectAsync(_context, _target.GetConnectionString(), default);

        Assert.Equal(1, second.Applied);
        var row = Assert.Single(await TargetRows());
        Assert.Equal("PRJ-0003", row.Plate);
        Assert.NotNull(row.Retired);
    }

    // A row the target schema will not take is a row whose rentals cannot move
    // either. It has to be named, and it must not stop the rows behind it.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RowsTheSchemaRefusesAreReportedWithoutStoppingTheRest()
    {
        Add("PRJ-0004", year: 1800);
        Add("PRJ-0005", id: "not-a-uuid");
        Add("PRJ-0006");
        await _context.SaveChangesAsync();

        var result = await MotorcycleProjector.ProjectAsync(_context, _target.GetConnectionString(), default);

        Assert.Equal(1, result.Applied);
        Assert.Equal("PRJ-0006", Assert.Single(await TargetRows()).Plate);
        Assert.Equal(2, result.Rejected.Count);
        Assert.Contains(result.Rejected, entry => entry.StartsWith("PRJ-0004", StringComparison.Ordinal));
        Assert.Contains(result.Rejected, entry => entry.Contains("not a UUID", StringComparison.Ordinal));
    }
}
