using Microsoft.EntityFrameworkCore;
using RiderManager.Data;
using RiderManager.Models;
using RiderManager.Repositories;
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RiderListing_IsCursorPagedBoundedAndLoadsStoredUrlsInOneQuery()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_database.GetConnectionString())
            .Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.MigrateAsync();
        context.Riders.AddRange(Enumerable.Range(0, 105).Select(index => new Rider
        {
            Id = $"rider-{index:D4}",
            UserId = $"user-{index:D4}",
            Email = $"rider-{index:D4}@example.test",
            Name = $"Rider {index:D4}",
            CNPJ = $"{index:D14}",
            DateOfBirth = DateTime.UtcNow.AddYears(-25),
            CNHNumber = $"{index:D11}",
            CNHType = "A",
            CNHUrl = index == 0
                ? new PresignedUrl
                {
                    Id = Guid.NewGuid().ToString(),
                    ObjectName = "cnh-rider-0000",
                    Url = "https://storage.example.test/cnh-rider-0000",
                    Expiry = DateTime.UtcNow.AddHours(1)
                }
                : null
        }));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var sql = new List<string>();
        var listingOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_database.GetConnectionString())
            .LogTo(sql.Add)
            .Options;
        await using var listingContext = new ApplicationDbContext(listingOptions);
        var repository = new RiderRepository(listingContext);

        var first = await repository.GetPageAsync(null, 1_000);
        sql.Clear();
        var second = await repository.GetPageAsync(first.NextCursor, 1_000);

        Assert.Equal(100, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.NotNull(first.NextCursor);
        Assert.Null(second.NextCursor);
        Assert.Equal("https://storage.example.test/cnh-rider-0000", first.Items[0].CNHUrl?.Url);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        Assert.Contains(sql, command => command.Contains("\"Id\" > @", StringComparison.Ordinal));
        Assert.DoesNotContain(sql, command => command.Contains("CASE", StringComparison.Ordinal));
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
