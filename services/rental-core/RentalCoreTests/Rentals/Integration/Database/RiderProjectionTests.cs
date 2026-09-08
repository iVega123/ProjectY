using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ProjectY.Events;
using RentalCoreTests.Integration;
using RentalOperations.Services;
using RentalOperations.Services.RabbitMQService;

namespace RentalCoreTests.Rentals.Integration.Database;

[Collection(RentalCoreDatabaseCollection.Name)]
public sealed class RiderProjectionTests(RentalCoreDatabase database)
{
    private static byte[] Event(string riderId, bool verified, long at, string name, string? eventId = null) =>
        new RiderEventV2
        {
            EventId = eventId ?? Guid.NewGuid().ToString("D"),
            RiderId = riderId,
            OccurredAtMs = at,
            Verified = verified,
            Name = name
        }.ToByteArray();

    private RiderProjection Projection(NpgsqlDataSource dataSource) => new(
        dataSource,
        new SqlInboxProcessor(dataSource, new InboxOptions(), TimeProvider.System),
        new ConfigurationBuilder().Build(),
        NullLogger<RiderProjection>.Instance);

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#inbox-convergence")]
    public async Task ReplayedVerificationChangesNothing()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var projection = Projection(dataSource);
        var rider = Guid.NewGuid().ToString("D");
        var eventId = Guid.NewGuid().ToString("D");

        await projection.HandleAsync(Event(rider, true, 200, "Ada Lovelace", eventId), CancellationToken.None);
        await projection.HandleAsync(Event(rider, true, 200, "Ada Lovelace", eventId), CancellationToken.None);

        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM rider_projection WHERE rider_id = '" + rider + "'");
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0015#carried-state")]
    public async Task OlderVerificationDoesNotRollTheProjectionBackwards()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var projection = Projection(dataSource);
        var rider = Guid.NewGuid().ToString("D");

        await projection.HandleAsync(Event(rider, false, 300, "Ada Byron"), CancellationToken.None);
        await projection.HandleAsync(Event(rider, true, 200, "Ada Lovelace"), CancellationToken.None);

        // Não há ordem entre tópicos, e nenhuma é assumida: o fato mais novo vence
        // pelo próprio carimbo, então um evento antigo reentregue não o desfaz.
        var view = await new SqlRiderProjectionStore(dataSource).GetAsync(rider, CancellationToken.None);
        Assert.NotNull(view);
        Assert.False(view!.Verified);
        Assert.Equal(300, view.VerifiedAtMs);
        Assert.Equal("Ada Byron", view.Name);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NewerVerificationAdvancesTheProjection()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var projection = Projection(dataSource);
        var rider = Guid.NewGuid().ToString("D");

        await projection.HandleAsync(Event(rider, false, 100, "Ada Byron"), CancellationToken.None);
        await projection.HandleAsync(Event(rider, true, 400, "Ada Lovelace"), CancellationToken.None);

        var view = await new SqlRiderProjectionStore(dataSource).GetAsync(rider, CancellationToken.None);
        Assert.True(view!.Verified);
        Assert.Equal(400, view.VerifiedAtMs);
        Assert.Equal("Ada Lovelace", view.Name);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StoreReadsWhatTheProjectionWrote()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var projection = Projection(dataSource);
        var rider = Guid.NewGuid().ToString("D");

        await projection.HandleAsync(Event(rider, true, 200, "Ada Lovelace"), CancellationToken.None);

        var store = new SqlRiderProjectionStore(dataSource);
        var view = await store.GetAsync(rider, CancellationToken.None);
        Assert.NotNull(view);
        Assert.True(view!.Verified);
        Assert.Equal("Ada Lovelace", view.Name);
        Assert.Null(await store.GetAsync("absent", CancellationToken.None));
    }

    // A pilha de benchmark semeia o piloto direto no banco, porque nada chega à
    // projeção pelo Kafka ali. Essa fixture (load/fixtures/reset-rentals.js) e
    // este modelo têm de continuar concordando: uma coluna renomeada só
    // apareceria como um load gate aquecendo contra "awaiting processing".
    [Fact]
    [Trait("Category", "Integration")]
    public async Task LoadFixtureRowShapeStaysReadable()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using (var seed = dataSource.CreateCommand("""
            INSERT INTO rider_projection (rider_id, verified, verified_at_ms, rider_name)
            VALUES ('load-rider', true, 1, 'Load rider')
            """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        var view = await new SqlRiderProjectionStore(dataSource).GetAsync("load-rider", CancellationToken.None);

        Assert.NotNull(view);
        Assert.True(view!.Verified);
        Assert.Equal("Load rider", view.Name);
    }
}
