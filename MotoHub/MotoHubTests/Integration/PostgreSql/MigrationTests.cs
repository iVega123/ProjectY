using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using MotoHub.Models;
using Testcontainers.PostgreSql;

namespace MotoHubTests.Integration.PostgreSql;

public sealed class MigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("moto_hub_migrations")
        .WithUsername("projecty")
        .Build();

    public Task InitializeAsync() => _database.StartAsync();

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrations_CreateCurrentSchemaFromEmptyDatabase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_database.GetConnectionString())
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.MigrateAsync();
        context.Motorcycles.Add(new Motorcycle
        {
            Year = 2026,
            Model = "Migration proof",
            LicensePlate = "MIG-0001",
            RegistrationDate = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(6, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(1, await context.Motorcycles.CountAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PlateMigration_NormalizesWinnerAndQuarantinesDuplicateMotorcycle()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_database.GetConnectionString())
            .Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.MigrateAsync("20260901122917_AddMotorcyclePaginationIndex");
        context.Motorcycles.AddRange(
            CreateMotorcycle("motorcycle-a", "abc1234", DateTime.UtcNow.AddDays(-2)),
            CreateMotorcycle("motorcycle-b", " ABC1234 ", DateTime.UtcNow.AddDays(-1)));
        await context.SaveChangesAsync();

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();

        var winner = await context.Motorcycles.SingleAsync(motorcycle => motorcycle.Id == "motorcycle-a");
        var duplicate = await context.Motorcycles.SingleAsync(motorcycle => motorcycle.Id == "motorcycle-b");
        Assert.Equal("ABC1234", winner.LicensePlate);
        Assert.Null(winner.RetiredAtUtc);
        Assert.StartsWith("~QUARANTINED~", duplicate.LicensePlate, StringComparison.Ordinal);
        Assert.NotNull(duplicate.RetiredAtUtc);
        Assert.Equal(
            2,
            await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM \"LegacyMotorcyclePlateReconciliations\"")
                .SingleAsync());
    }

    private static Motorcycle CreateMotorcycle(string id, string licensePlate, DateTime registrationDate) => new()
    {
        Id = id,
        Year = 2020,
        Model = "Legacy motorcycle",
        LicensePlate = licensePlate,
        RegistrationDate = registrationDate
    };
}

public sealed class DbContextConstructionTests
{
    [Fact]
    public void Constructor_DoesNotAccessDatabase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=database.invalid;Timeout=1;Database=MotoHubDB;Username=projecty")
            .Options;

        using var context = new ApplicationDbContext(options);

        Assert.NotNull(context);
    }
}
