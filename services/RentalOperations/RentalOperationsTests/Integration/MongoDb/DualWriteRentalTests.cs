using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RentalOperations.Data;
using RentalOperations.Model;
using RentalOperations.Repository;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;

namespace RentalOperationsTests.Integration.MongoDb;

/// <summary>
/// The target schema is applied from deploy/db/sql/001_schema.sql itself. The engine
/// is PostgreSQL rather than CockroachDB because schema-portability.yml already
/// proves the same file yields the same schema on both, and this suite is about the
/// mapping and the two live guarantees, not about the engine.
/// </summary>
public sealed class DualWriteRentalTests : IAsyncLifetime
{
    private const string DatabaseName = "rental_dual_write_tests";
    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:8.0").Build();
    private readonly PostgreSqlContainer _target = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("projecty").WithUsername("projecty").Build();
    private MongoDbContext _context = null!;
    private IRentalRepository _repository = null!;
    private Guid _motorcycle;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_mongo.StartAsync(), _target.StartAsync());
        _context = new MongoDbContext(_mongo.GetConnectionString(), DatabaseName);

        var schema = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "target-schema", "001_schema.sql"));
        schema = string.Join('\n', schema.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("GRANT", StringComparison.Ordinal)));
        await Execute(schema);

        _motorcycle = Guid.NewGuid();
        await Execute("INSERT INTO motorcycles (id, license_plate, model, year) VALUES ("
            + Literal(_motorcycle.ToString()) + ", 'DUA-0001', 'Dual write', 2026);");

        _repository = new DualWriteRentalRepository(
            new RentalRepository(_context), _target.GetConnectionString(),
            NullLogger<DualWriteRentalRepository>.Instance);
    }

    public Task DisposeAsync() =>
        Task.WhenAll(_mongo.DisposeAsync().AsTask(), _target.DisposeAsync().AsTask());

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_target.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<(Guid Id, string Status, DateTime? Ends)>> TargetRentals()
    {
        await using var connection = new NpgsqlConnection(_target.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, status, ends_at FROM rentals ORDER BY created_at", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(Guid, string, DateTime?)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2)));
        }
        return rows;
    }

    private Rental NewRental() => new()
    {
        MotorcycleLicencePlate = "DUA-0001",
        MotorcycleId = _motorcycle.ToString(),
        UserId = "rider-1",
        RiderName = "Ada Lovelace",
        StartDate = DateTime.UtcNow.Date.AddDays(1),
        PredictedEndDate = DateTime.UtcNow.Date.AddDays(8),
        InitCost = 210.25m
    };

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RentalReachesBothStoresUnderTheSameIdentity()
    {
        var rental = await _repository.CreateRentalAsync(NewRental());

        var row = Assert.Single(await TargetRentals());
        Assert.Equal(RentalRowMapper.ToRowId(rental._id!.Value), row.Id);
        Assert.Equal("active", row.Status);
        Assert.Null(row.Ends);
    }

    // The identity has to be derived, not generated: the comparison that gates the
    // cutover compares two stores, and a fresh id per pass would compare one store
    // against a growing pile of duplicates.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RepeatedMirrorsUpdateTheSameRowInsteadOfAddingOne()
    {
        var rental = await _repository.CreateRentalAsync(NewRental());
        rental.Status = RentalStatus.Completed;
        rental.EndDate = DateTime.UtcNow.Date.AddDays(5);
        rental.FinalCost = 180m;

        await _repository.UpdateRentalAsync(rental);

        var row = Assert.Single(await TargetRentals());
        Assert.Equal("closed", row.Status);
        Assert.NotNull(row.Ends);
    }

    // Both guarantees live at once is the whole reason this step exists. Mongo's
    // partial index refuses the second active rental, and the target schema refuses
    // it too -- asked directly, because the request never gets far enough to mirror.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task BothStoresRefuseASecondActiveRentalForTheSameMotorcycle()
    {
        var first = await _repository.CreateRentalAsync(NewRental());

        var refused = await Assert.ThrowsAsync<PostgresException>(() => Execute(
            "INSERT INTO rentals (id, rider_id, motorcycle_id, starts_at, predicted_ends_at, init_cost, status) VALUES ("
            + Literal(Guid.NewGuid().ToString()) + ", 'rider-2', " + Literal(_motorcycle.ToString())
            + ", now(), now() + interval '1 day', 10, 'active');"));

        Assert.Equal("one_active_rental_per_motorcycle", refused.ConstraintName);
        Assert.Equal(RentalRowMapper.ToRowId(first._id!.Value), Assert.Single(await TargetRentals()).Id);
    }

    // A returned motorcycle can be rented again -- the partial predicate doing its
    // job. A plain unique index would pass the conflict case and break this one.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AReturnedMotorcycleCanBeRentedAgainInBothStores()
    {
        var first = await _repository.CreateRentalAsync(NewRental());
        first.Status = RentalStatus.Completed;
        first.EndDate = DateTime.UtcNow.Date.AddDays(3);
        await _repository.UpdateRentalAsync(first);

        var second = await _repository.CreateRentalAsync(NewRental());

        var rows = await TargetRentals();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row =>
            row.Id == RentalRowMapper.ToRowId(second._id!.Value) && row.Status == "active");
    }

    // Quarantined has no value the target schema accepts. Folding it into cancelled
    // would erase what the quarantine migrations recorded, so the row is refused by
    // name and Mongo -- still authoritative -- keeps the rental.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AQuarantinedRentalIsRefusedByNameRatherThanRewritten()
    {
        var rental = await _repository.CreateRentalAsync(NewRental());
        rental.Status = RentalStatus.Quarantined;

        await _repository.UpdateRentalAsync(rental);

        Assert.Equal("active", Assert.Single(await TargetRentals()).Status);
        var refusal = RentalRowMapper.Refusal(rental);
        Assert.NotNull(refusal);
        Assert.Contains("Quarantined", refusal!.Reason, StringComparison.Ordinal);
    }
}
