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
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await context.Motorcycles.CountAsync());
    }
}

public sealed class DbContextConstructionTests
{
    [Fact]
    public void Constructor_DoesNotAccessDatabase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=database.invalid;Timeout=1;Database=moto_hub;Username=projecty")
            .Options;

        using var context = new ApplicationDbContext(options);

        Assert.NotNull(context);
    }
}
