using AuthGate.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace AuthGateTests.Integration.PostgreSql;

public sealed class MigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("auth_gate_migrations")
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

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(3, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(2, await context.Roles.CountAsync());
    }
}

public sealed class DbContextConstructionTests
{
    [Fact]
    public void Constructor_DoesNotAccessDatabase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=database.invalid;Timeout=1;Database=AuthGateDB;Username=projecty")
            .Options;

        using var context = new ApplicationDbContext(options);

        Assert.NotNull(context);
    }
}
