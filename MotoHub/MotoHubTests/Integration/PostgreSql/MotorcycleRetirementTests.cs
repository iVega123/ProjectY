using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using MotoHub.Models;
using MotoHub.Repositories;
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

        Assert.Empty(repository.GetAll());
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
        Assert.Empty(repository.GetAll());
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
