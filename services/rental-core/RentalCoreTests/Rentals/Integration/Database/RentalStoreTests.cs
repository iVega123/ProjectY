using Npgsql;
using ProjectY.Events;
using RentalCoreTests.Integration;
using RentalOperations.Domain;
using RentalOperations.Model;
using RentalOperations.Repository;

namespace RentalCoreTests.Rentals.Integration.Database;

[Collection(RentalCoreDatabaseCollection.Name)]
public sealed class RentalStoreTests(RentalCoreDatabase database)
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#database-serialized-rental-claim")]
    public async Task ConcurrentRentalsForSameMotorcycle_OneWinsAndTheDatabaseRefusesTheOther()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("CON0C01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var repository = new SqlRentalRepository(dataSource);

        // Sem lock na aplicação e sem checagem prévia: as duas escritas chegam ao
        // INSERT. Se as duas passassem, a garantia central do sistema não existiria.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 2).Select(async index =>
        {
            await start.Task;
            try
            {
                await repository.CreateRentalAsync(NewRental(motorcycleId, "rider-" + index));
                return (Won: true, Conflict: false);
            }
            catch (ActiveRentalConflictException)
            {
                return (Won: false, Conflict: true);
            }
        }).ToArray();
        start.SetResult();

        var outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes, outcome => outcome.Won);
        Assert.Single(outcomes, outcome => outcome.Conflict);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM rentals WHERE status = 'active'"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#database-serialized-rental-claim")]
    public async Task ReturnedMotorcycle_CanBeRentedAgain()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("REL0C01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var repository = new SqlRentalRepository(dataSource);

        var first = await repository.CreateRentalAsync(NewRental(motorcycleId, "rider-first"));
        first.Status = RentalStatus.Completed;
        first.EndDate = first.PredictedEndDate;
        first.FinalCost = 210m;
        await repository.UpdateRentalAsync(first);

        // O predicado é o que torna a restrição correta. Um índice único comum
        // passaria no teste de conflito acima e deixaria a moto inalugável para
        // sempre depois da primeira devolução.
        var second = await repository.CreateRentalAsync(NewRental(motorcycleId, "rider-second"));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM rentals WHERE status = 'active'"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#transactional-outbox")]
    public async Task CreatingARental_WritesItsOutboxRowInTheSameTransaction()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("OUT0B01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var repository = new SqlRentalRepository(dataSource);

        var rental = await repository.CreateRentalAsync(NewRental(motorcycleId, "rider-outbox"));

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT topic, payload, aggregate_id FROM outbox WHERE aggregate_type = 'rental'", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("rental.started", reader.GetString(0));
        var published = RentalEvent.Parser.ParseFrom((byte[])reader[1]);
        Assert.Equal(rental.Id.ToString(), published.RentalId);
        Assert.Equal(motorcycleId.ToString(), published.MotorcycleId);
        Assert.Equal(motorcycleId.ToString(), reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#transactional-outbox")]
    public async Task RefusedRental_LeavesNeitherTheRowNorTheEvent()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("ROL0B01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var repository = new SqlRentalRepository(dataSource);
        await repository.CreateRentalAsync(NewRental(motorcycleId, "rider-first"));

        await Assert.ThrowsAsync<ActiveRentalConflictException>(
            () => repository.CreateRentalAsync(NewRental(motorcycleId, "rider-second")));

        // Um evento anunciando um aluguel que não existe é pior do que nenhum
        // evento: o consumidor não tem como descobrir que ele foi desfeito.
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM rentals"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM outbox"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentSettlements_LeaveOneWinnerAndOneReportedConflict()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("SET0C01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var repository = new SqlRentalRepository(dataSource);
        var rental = await repository.CreateRentalAsync(NewRental(motorcycleId, "rider-settle"));

        async Task<bool> SettleAsync()
        {
            var snapshot = await repository.GetRentalByIdAsync(rental.Id.ToString());
            snapshot!.Status = RentalStatus.Completed;
            snapshot.EndDate = snapshot.PredictedEndDate;
            snapshot.FinalCost = 210m;
            try
            {
                await repository.UpdateRentalAsync(snapshot);
                return true;
            }
            catch (RentalSettlementConflictException)
            {
                return false;
            }
        }

        var outcomes = await Task.WhenAll(SettleAsync(), SettleAsync());

        Assert.Single(outcomes, settled => settled);
        Assert.Single(outcomes, settled => !settled);
        // Um rental.closed por fechamento, não um por tentativa.
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM outbox WHERE topic = 'rental.closed'"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OverlapQuery_RejectsPeriodsInsideCompletedRentalHistory()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("OVL0P01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var repository = new SqlRentalRepository(dataSource);
        var start = DateTime.UtcNow.Date.AddDays(1);
        var rental = NewRental(motorcycleId, "rider-history");
        rental.StartDate = start;
        rental.PredictedEndDate = start.AddDays(7);
        var created = await repository.CreateRentalAsync(rental);
        created.Status = RentalStatus.Completed;
        created.EndDate = start.AddDays(7);
        created.FinalCost = 210m;
        await repository.UpdateRentalAsync(created);

        // Um aluguel devolvido continua ocupando a agenda até onde ocupou. Sem
        // isto, dois aluguéis do mesmo período conviveriam desde que o primeiro
        // já estivesse fechado.
        Assert.True(await repository.HasOverlappingRentalAsync(
            motorcycleId, start.AddDays(2), start.AddDays(4)));
        Assert.False(await repository.HasOverlappingRentalAsync(
            motorcycleId, start.AddDays(8), start.AddDays(10)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UserListing_IsCursorPagedAndStableAcrossPages()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var repository = new SqlRentalRepository(dataSource);
        for (var index = 0; index < 7; index++)
        {
            var motorcycleId = await database.AddMotorcycleAsync($"PAG{index:D4}");
            await repository.CreateRentalAsync(NewRental(motorcycleId, "rider-paged"));
        }

        var first = await repository.GetRentalsByUserId("rider-paged", null, 3);
        var second = await repository.GetRentalsByUserId("rider-paged", first.NextCursor, 3);
        var third = await repository.GetRentalsByUserId("rider-paged", second.NextCursor, 3);

        Assert.Equal(3, first.Items.Count);
        Assert.Equal(3, second.Items.Count);
        Assert.Single(third.Items);
        Assert.Null(third.NextCursor);
        var seen = first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Id).ToList();
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadingARental_ResolvesThePlateThroughTheMotorcycle()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("JOI0N01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var repository = new SqlRentalRepository(dataSource);
        var rental = await repository.CreateRentalAsync(NewRental(motorcycleId, "rider-join"));

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var rename = new NpgsqlCommand(
            "UPDATE motorcycles SET license_plate = 'JOI0N02' WHERE id = @id", connection))
        {
            rename.Parameters.AddWithValue("id", motorcycleId);
            await rename.ExecuteNonQueryAsync();
        }

        // A placa é junção, não cópia: corrigir a placa corrige o histórico
        // inteiro, em vez de bifurcá-lo entre aluguéis antigos e novos.
        var reread = await repository.GetRentalByIdAsync(rental.Id.ToString());
        Assert.Equal("JOI0N02", reread!.MotorcycleLicencePlate);
    }

    private static Rental NewRental(Guid motorcycleId, string riderId)
    {
        var start = DateTime.UtcNow.Date.AddDays(1);
        return new Rental
        {
            MotorcycleId = motorcycleId,
            UserId = riderId,
            RiderName = "Ada Lovelace",
            MotorcycleLicencePlate = "UNU0S3D",
            StartDate = start,
            PredictedEndDate = start.AddDays(7),
            InitCost = 210m
        };
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
