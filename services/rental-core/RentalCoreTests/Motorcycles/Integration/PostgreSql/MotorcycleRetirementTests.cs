using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using MotoHub.Models;
using MotoHub.Repositories;
using MotoHub.Services;
using Npgsql;
using RentalCoreTests.Integration;
using RentalOperations.Model;
using RentalOperations.Repository;

namespace MotoHubTests.Integration.PostgreSql;

[Collection(RentalCoreDatabaseCollection.Name)]
public sealed class MotorcycleRetirementTests(RentalCoreDatabase database)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RetiredMotorcycle_DisappearsFromListingButRemainsResolvable()
    {
        await database.ResetAsync();
        await using var context = CreateContext();
        var motorcycle = new Motorcycle
        {
            LicensePlate = "RET0001",
            Model = "Historical model",
            Year = 2025,
            RegistrationDate = DateTime.UtcNow
        };
        context.Motorcycles.Add(motorcycle);
        await context.SaveChangesAsync();
        var repository = new MotorcycleRepository(context);

        var retiredAtUtc = DateTime.UtcNow;
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        Assert.Equal(
            MotorcycleRetirementResult.Retired,
            await new MotorcycleRetirement(dataSource).RetireAsync(
                motorcycle.Id,
                retiredAtUtc,
                MotorcycleRetirementReasons.RequestedByAdministrator));

        context.ChangeTracker.Clear();
        Assert.Empty((await repository.GetPageAsync(null, null)).Items);
        var historical = await repository.GetByLicensePlateAsync(motorcycle.LicensePlate);
        Assert.NotNull(historical);
        Assert.Equal(retiredAtUtc, historical!.RetiredAtUtc!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(MotorcycleRetirementReasons.RequestedByAdministrator, historical.RetirementReason);
    }

    /// <summary>
    /// A corrida de retirada do ADR 0010, agora decidida pelo banco.
    ///
    /// Era um protocolo: reservar uma marca de aposentadoria num store, apagar a
    /// moto no outro, e viver com a janela entre os dois. Aqui o aluguel e a
    /// aposentadoria disputam a mesma moto ao mesmo tempo, e o estado que não
    /// pode existir -- moto aposentada com aluguel ativo -- não existe, porque
    /// as duas escritas avaliam a existência uma da outra dentro de si mesmas.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0010#retirement-race")]
    public async Task RetirementAndRental_RacingForTheSameMotorcycle_LeaveExactlyOneWinner()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("RAC0E01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var retirement = new MotorcycleRetirement(dataSource);
        var rentals = new SqlRentalRepository(dataSource);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var retiring = Task.Run(async () =>
        {
            await start.Task;
            return await retirement.RetireAsync(
                motorcycleId, DateTime.UtcNow, MotorcycleRetirementReasons.RequestedByAdministrator);
        });
        var renting = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                await rentals.CreateRentalAsync(NewRental(motorcycleId));
                return true;
            }
            catch (Exception error) when (error is RentalOperations.Domain.ActiveRentalConflictException
                                              or RentalOperations.Domain.MotorcycleRetiredException
                                              or PostgresException)
            {
                // Perder para a aposentadoria é o resultado esperado quando ela
                // chega primeiro, e ela diz isso pelo nome em vez de por um erro
                // genérico do banco.
                return false;
            }
        });
        start.SetResult();

        var retired = await retiring;
        var rented = await renting;

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var check = new NpgsqlCommand("""
            SELECT count(*)
              FROM rentals AS r
              JOIN motorcycles AS m ON m.id = r.motorcycle_id
             WHERE r.status = 'active' AND m.retired_at IS NOT NULL
            """, connection);
        Assert.Equal(0L, await check.ExecuteScalarAsync());
        Assert.True(retired == MotorcycleRetirementResult.Retired || rented,
            "Neither the retirement nor the rental was allowed to proceed.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0010#retirement-race")]
    public async Task RetirementIsRefused_WhileAnActiveRentalExists()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("BUS0Y01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await new SqlRentalRepository(dataSource).CreateRentalAsync(NewRental(motorcycleId));

        var result = await new MotorcycleRetirement(dataSource).RetireAsync(
            motorcycleId, DateTime.UtcNow, MotorcycleRetirementReasons.RequestedByAdministrator);

        Assert.Equal(MotorcycleRetirementResult.ActiveRental, result);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RetiringTwice_ReportsTheSecondAsAlreadyRetired()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("TWI0C01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var retirement = new MotorcycleRetirement(dataSource);

        Assert.Equal(
            MotorcycleRetirementResult.Retired,
            await retirement.RetireAsync(motorcycleId, DateTime.UtcNow, "first"));
        Assert.Equal(
            MotorcycleRetirementResult.AlreadyRetired,
            await retirement.RetireAsync(motorcycleId, DateTime.UtcNow, "second"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Listing_IsCursorPagedBoundedAndUsesAnIndex()
    {
        await database.ResetAsync();
        await using var context = CreateContext();
        context.Motorcycles.AddRange(Enumerable.Range(0, 105).Select(index => new Motorcycle
        {
            Id = new Guid($"00000000-0000-0000-0000-{index:D12}"),
            LicensePlate = $"PAG{index:D4}",
            Model = "Pagination proof",
            Year = 2026,
            RegistrationDate = DateTime.UtcNow
        }));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var sql = new List<string>();
        await using var listingContext = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(database.ConnectionString)
                .LogTo(sql.Add)
                .Options);
        var repository = new MotorcycleRepository(listingContext);

        var first = await repository.GetPageAsync(null, 1_000);
        sql.Clear();
        var second = await repository.GetPageAsync(first.NextCursor, 1_000);

        Assert.Equal(100, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.NotNull(first.NextCursor);
        Assert.Null(second.NextCursor);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        Assert.Contains(sql, command => command.Contains("id > @", StringComparison.Ordinal));
        Assert.DoesNotContain(sql, command => command.Contains("CASE", StringComparison.Ordinal));

        // O índice é de 002_rental_core.sql, e carrega o que a migração
        // AddMotorcyclePaginationIndex fechava: sem ele a paginação varre a
        // tabela inteira a cada página.
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var indexQuery = new NpgsqlCommand(
            "SELECT indexname FROM pg_indexes WHERE tablename = 'motorcycles' AND indexname = 'motorcycles_active_page'",
            connection);
        Assert.Equal("motorcycles_active_page", await indexQuery.ExecuteScalarAsync());
    }

    private static Rental NewRental(Guid motorcycleId)
    {
        var start = DateTime.UtcNow.Date.AddDays(1);
        return new Rental
        {
            MotorcycleId = motorcycleId,
            UserId = "rider-retirement",
            MotorcycleLicencePlate = "UNU0S3D",
            StartDate = start,
            PredictedEndDate = start.AddDays(7),
            InitCost = 210m
        };
    }

    private ApplicationDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(database.ConnectionString)
            .Options);
}

public sealed class DbContextConstructionTests
{
    [Fact]
    public void Constructor_DoesNotAccessDatabase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=database.invalid;Timeout=1;Database=projecty;Username=projecty")
            .Options;

        using var context = new ApplicationDbContext(options);

        Assert.NotNull(context);
    }
}
