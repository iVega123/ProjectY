using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Npgsql;
using RentalOperations.Data;
using RentalOperations.Model;
using RentalOperations.Repository;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;

namespace RentalOperationsTests.Integration.MongoDb;

/// <summary>
/// The backfill and the comparison that gates the cutover, against both stores.
/// The target schema is applied from deploy/db/sql/001_schema.sql itself.
/// </summary>
public sealed class RentalMigrationTests : IAsyncLifetime
{
    private const string DatabaseName = "rental_migration_tests";
    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:8.0").Build();
    private readonly PostgreSqlContainer _target = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("projecty").WithUsername("projecty").Build();
    private MongoDbContext _context = null!;
    private NpgsqlDataSource _dataSource = null!;
    private RentalTargetWriter _writer = null!;
    private IMongoCollection<Rental> _rentals = null!;
    private Guid _motorcycle;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_mongo.StartAsync(), _target.StartAsync());
        _context = new MongoDbContext(_mongo.GetConnectionString(), DatabaseName);
        _rentals = _context.Database.GetCollection<Rental>("Rentals");

        var schema = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "target-schema", "001_schema.sql"));
        schema = string.Join('\n', schema.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("GRANT", StringComparison.Ordinal)));
        await Execute(schema);

        _motorcycle = Guid.NewGuid();
        await Execute("INSERT INTO motorcycles (id, license_plate, model, year) VALUES ('"
            + _motorcycle + "', 'MIG-0001', 'Migration', 2026);");

        _dataSource = NpgsqlDataSource.Create(_target.GetConnectionString());
        _writer = new RentalTargetWriter(_dataSource, NullLogger<RentalTargetWriter>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_mongo.DisposeAsync().AsTask(), _target.DisposeAsync().AsTask());
    }

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_target.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Rental> Seed(RentalStatus status = RentalStatus.Active, decimal final = 0m)
    {
        var rental = new Rental
        {
            MotorcycleLicencePlate = "MIG-0001",
            MotorcycleId = _motorcycle.ToString(),
            UserId = "rider-1",
            StartDate = DateTime.UtcNow.Date.AddDays(1),
            PredictedEndDate = DateTime.UtcNow.Date.AddDays(8),
            InitCost = 210.25m,
            FinalCost = final,
            Status = status
        };
        await _rentals.InsertOneAsync(rental);
        return rental;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BackfillMovesWhatMongoAlreadyHolds()
    {
        await Seed();
        await Seed(RentalStatus.Completed, final: 180m);

        var report = await RentalMigration.BackfillAsync(_context, _writer, CancellationToken.None);

        Assert.Equal(2, report.Total);
        Assert.Equal(2, report.Applied);
        Assert.Empty(report.Refused);
    }

    // Running it twice must not double anything: the identity is derived from the
    // document, so the second pass updates the same rows it wrote the first time.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task BackfillIsSafeToRunAgain()
    {
        await Seed();

        await RentalMigration.BackfillAsync(_context, _writer, CancellationToken.None);
        var second = await RentalMigration.BackfillAsync(_context, _writer, CancellationToken.None);
        var comparison = await RentalMigration.CompareAsync(_context, _dataSource, CancellationToken.None);

        Assert.Equal(1, second.Applied);
        Assert.Equal(1, comparison.TargetRows);
        Assert.True(comparison.IsClean);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComparisonIsCleanOnceBothStoresAgree()
    {
        await Seed();
        await Seed(RentalStatus.Completed, final: 180m);
        await RentalMigration.BackfillAsync(_context, _writer, CancellationToken.None);

        var comparison = await RentalMigration.CompareAsync(_context, _dataSource, CancellationToken.None);

        Assert.True(comparison.IsClean);
        Assert.Equal(2, comparison.MongoRows);
        Assert.Equal(2, comparison.TargetRows);
    }

    // A comparison that cannot fail is not a gate. This changes one store behind the
    // other's back, exactly as a missed dual write would, and the report has to say so.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComparisonNamesAFieldTheStoresDisagreeAbout()
    {
        var rental = await Seed(RentalStatus.Completed, final: 180m);
        await RentalMigration.BackfillAsync(_context, _writer, CancellationToken.None);
        await Execute("UPDATE rentals SET final_cost = 999.00;");

        var comparison = await RentalMigration.CompareAsync(_context, _dataSource, CancellationToken.None);

        Assert.False(comparison.IsClean);
        var divergence = Assert.Single(comparison.Divergent);
        Assert.Equal("final_cost", divergence.Field);
        Assert.Equal(rental._id!.Value.ToString(), divergence.RentalId);
        Assert.Equal("180.00", divergence.Mongo);
        Assert.Equal("999.00", divergence.Target);
    }

    // Absence and disagreement fail for different reasons and are fixed in different
    // places, so the report keeps them apart rather than counting one number.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComparisonSeparatesARowThatNeverArrivedFromOneThatArrivedWrong()
    {
        var rental = await Seed();

        var comparison = await RentalMigration.CompareAsync(_context, _dataSource, CancellationToken.None);

        Assert.False(comparison.IsClean);
        Assert.Empty(comparison.Divergent);
        Assert.Equal(rental._id!.Value.ToString(), Assert.Single(comparison.Missing));
    }

    // Quarantine does not cross into the target schema. The backfill refuses the row
    // by name so the cutover cannot be declared while one still exists, rather than
    // rewriting it as cancelled and asserting a decision nobody made.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task BackfillRefusesAQuarantinedRentalByName()
    {
        var rental = await Seed(RentalStatus.Quarantined);

        var report = await RentalMigration.BackfillAsync(_context, _writer, CancellationToken.None);

        Assert.Equal(0, report.Applied);
        var refusal = Assert.Single(report.Refused);
        Assert.Contains(rental._id!.Value.ToString(), refusal, StringComparison.Ordinal);
        Assert.Contains("Quarantined", refusal, StringComparison.Ordinal);
    }

    // The dropped column has to be recoverable for dropping it to be honest:
    // AdditionalCostsOrSavings is exactly final_cost - init_cost.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheDroppedSettlementColumnIsArithmeticOnTwoThatRemain()
    {
        var rental = await Seed(RentalStatus.Completed, final: 180m);
        rental.AdditionalCostsOrSavings = rental.FinalCost - rental.InitCost;
        await _rentals.ReplaceOneAsync(candidate => candidate._id == rental._id, rental);
        await RentalMigration.BackfillAsync(_context, _writer, CancellationToken.None);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT final_cost - init_cost FROM rentals", connection);
        var derived = (decimal)(await command.ExecuteScalarAsync())!;

        Assert.Equal(rental.AdditionalCostsOrSavings, derived);
    }
}
