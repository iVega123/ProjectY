using AuthGate.Data;
using AuthGate.Model;
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
        Assert.Equal(4, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(2, await context.Roles.CountAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CnpjMigration_NormalizesWinnerAndQuarantinesDuplicateAccount()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_database.GetConnectionString())
            .Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.MigrateAsync("20260831181244_ClaimOutboxMessages");
        context.Set<RiderUser>().AddRange(
            CreateRider("auth-user-a", "92.805.586/0001-80", "12345678901"),
            CreateRider("auth-user-b", "92805586000180", "12345678902"));
        await context.SaveChangesAsync();

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();

        var winner = await context.Set<RiderUser>().SingleAsync(rider => rider.Id == "auth-user-a");
        var duplicate = await context.Set<RiderUser>().SingleAsync(rider => rider.Id == "auth-user-b");
        Assert.Equal("92805586000180", winner.CNPJ);
        Assert.False(winner.LockoutEnabled);
        Assert.StartsWith("QUAR:", duplicate.CNPJ, StringComparison.Ordinal);
        Assert.True(duplicate.LockoutEnabled);
        Assert.Equal(
            2,
            await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM \"LegacyRiderCnpjReconciliations\"")
                .SingleAsync());
    }

    private static RiderUser CreateRider(string id, string cnpj, string cnh) => new()
    {
        Id = id,
        UserName = $"{id}@example.test",
        Email = $"{id}@example.test",
        UserType = UserType.Rider,
        CNPJ = cnpj,
        Name = id,
        DateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CNHNumber = cnh,
        CNHType = TipoCNH.A
    };
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
