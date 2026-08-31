using Microsoft.EntityFrameworkCore;
using RiderManager.Data;
using RiderManager.Models;
using Testcontainers.PostgreSql;

namespace RiderManagerTests.Integration.PostgreSql;

public sealed class MigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("rider_manager_migrations")
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
        context.Riders.Add(new Rider
        {
            Id = Guid.NewGuid().ToString(),
            UserId = Guid.NewGuid().ToString(),
            Email = "migration@example.test",
            Name = "Migration proof",
            CNPJ = "12345678000199",
            DateOfBirth = DateTime.UtcNow.AddYears(-25),
            CNHNumber = "12345678901",
            CNHType = "A"
        });
        await context.SaveChangesAsync();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(1, await context.Riders.CountAsync());
        Assert.Empty(await context.InboxMessages.ToListAsync());
    }
}

public sealed class DbContextConstructionTests
{
    [Fact]
    public void Constructor_DoesNotAccessDatabase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=database.invalid;Timeout=1;Database=rider_manager;Username=projecty")
            .Options;

        using var context = new ApplicationDbContext(options);

        Assert.NotNull(context);
    }
}
