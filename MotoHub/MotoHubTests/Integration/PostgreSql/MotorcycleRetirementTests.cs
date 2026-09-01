using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using MotoHub.Models;
using MotoHub.Repositories;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MotoHubTests.Integration.PostgreSql;

public sealed class MotorcycleRetirementTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("moto_hub_retirement")
        .WithUsername("projecty")
        .Build();

    public Task InitializeAsync() => _database.StartAsync();

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RetiredMotorcycle_DisappearsFromListingButRemainsResolvable()
    {
        await using var context = await CreateContextAsync();
        var motorcycle = new Motorcycle
        {
            LicensePlate = "RET-0001",
            Model = "Historical model",
            Year = 2025,
            RegistrationDate = DateTime.UtcNow
        };
        context.Motorcycles.Add(motorcycle);
        await context.SaveChangesAsync();
        var repository = new MotorcycleRepository(context);

        var retiredAtUtc = DateTime.UtcNow;
        Assert.True(await repository.RetireAsync(
            motorcycle.Id,
            retiredAtUtc,
            MotorcycleRetirementReasons.RequestedByAdministrator));

        Assert.Empty((await repository.GetPageAsync(null, null)).Items);
        var historical = await repository.GetByLicensePlateAsync(motorcycle.LicensePlate);
        Assert.NotNull(historical);
        Assert.Equal(retiredAtUtc, historical.RetiredAtUtc);
        Assert.Equal(MotorcycleRetirementReasons.RequestedByAdministrator, historical.RetirementReason);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MissingLegacyReference_IsBackfilledAsRetiredPlaceholderOnce()
    {
        await using var context = await CreateContextAsync();
        var repository = new MotorcycleRepository(context);
        var migratedAtUtc = DateTime.UtcNow;

        await repository.EnsureHistoricalReferenceAsync("OLD-0001", migratedAtUtc);
        await repository.EnsureHistoricalReferenceAsync("OLD-0001", migratedAtUtc.AddMinutes(1));

        var historical = await context.Motorcycles.SingleAsync();
        Assert.Equal("OLD-0001", historical.LicensePlate);
        Assert.Equal(migratedAtUtc, historical.RetiredAtUtc);
        Assert.Equal(MotorcycleRetirementReasons.LegacyOrphanBackfill, historical.RetirementReason);
        Assert.Empty((await repository.GetPageAsync(null, null)).Items);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Listing_IsCursorPagedBoundedAndUsesAnIndex()
    {
        await using var context = await CreateContextAsync();
        context.Motorcycles.AddRange(Enumerable.Range(0, 105).Select(index => new Motorcycle
        {
            Id = $"motorcycle-{index:D4}",
            LicensePlate = $"PAGE-{index:D4}",
            Model = "Pagination proof",
            Year = 2026,
            RegistrationDate = DateTime.UtcNow
        }));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var repository = new MotorcycleRepository(context);

        var first = await repository.GetPageAsync(null, 1_000);
        var second = await repository.GetPageAsync(first.NextCursor, 1_000);

        Assert.Equal(100, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.NotNull(first.NextCursor);
        Assert.Null(second.NextCursor);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));

        context.Motorcycles.AddRange(Enumerable.Range(105, 10_000).Select(index => new Motorcycle
        {
            Id = $"motorcycle-{index:D5}",
            LicensePlate = $"PLAN-{index:D5}",
            Model = "Query plan proof",
            Year = 2026,
            RegistrationDate = DateTime.UtcNow
        }));
        await context.SaveChangesAsync();

        await using var connection = new NpgsqlConnection(_database.GetConnectionString());
        await connection.OpenAsync();
        await using (var analyzeCommand = new NpgsqlCommand("ANALYZE \"Motorcycles\"", connection))
        {
            await analyzeCommand.ExecuteNonQueryAsync();
        }
        await using var indexCommand = new NpgsqlCommand(
            "SELECT indexname FROM pg_indexes WHERE tablename = 'Motorcycles' AND indexname = 'IX_Motorcycles_Active_Id'",
            connection);
        Assert.Equal("IX_Motorcycles_Active_Id", await indexCommand.ExecuteScalarAsync());

        await using var explainCommand = new NpgsqlCommand(
            "EXPLAIN SELECT \"Id\" FROM \"Motorcycles\" WHERE \"RetiredAtUtc\" IS NULL AND \"Id\" > 'motorcycle-0000' ORDER BY \"Id\" LIMIT 101",
            connection);
        var plan = new List<string>();
        await using var reader = await explainCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(0));
        }

        Assert.Contains(plan, line => line.Contains("Index", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("Seq Scan", StringComparison.Ordinal));
    }

    private async Task<ApplicationDbContext> CreateContextAsync()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_database.GetConnectionString())
            .Options;
        var context = new ApplicationDbContext(options);
        await context.Database.MigrateAsync();
        return context;
    }
}
