using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ProjectY.Events;
using RentalCoreTests.Integration;
using RentalOperations.Repository;
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

    // O leitor da projeção é a criação de aluguel: o piloto volta junto com as
    // outras pré-condições. Ler por ali é afirmar o que a decisão enxerga, e não
    // o que uma consulta paralela enxergaria.
    private static async Task<RiderView?> ReadRiderAsync(NpgsqlDataSource dataSource, string riderId)
    {
        var start = DateTime.UtcNow.Date.AddDays(1);
        var preconditions = await new SqlRentalRepository(dataSource)
            .ReadCreationPreconditionsAsync(riderId, Guid.NewGuid(), start, start.AddDays(7));
        return preconditions.Rider;
    }

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
        var view = await ReadRiderAsync(dataSource, rider);
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

        var view = await ReadRiderAsync(dataSource, rider);
        Assert.True(view!.Verified);
        Assert.Equal(400, view.VerifiedAtMs);
        Assert.Equal("Ada Lovelace", view.Name);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0015#carried-state")]
    public async Task RevokingARiderUnverifiesTheProjection()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var projection = Projection(dataSource);
        var rider = Guid.NewGuid().ToString("D");

        await projection.HandleAsync(Event(rider, true, 100, "Ada Lovelace"), CancellationToken.None);
        await projection.HandleAsync(Event(rider, false, 500, "Ada Lovelace"), CancellationToken.None);

        // Apagar o piloto no RiderManager não alcança este banco. O que alcança é
        // o fato, e ele tem de derrubar a linha -- senão a autorização local
        // continua dizendo sim para quem não existe mais.
        var view = await ReadRiderAsync(dataSource, rider);
        Assert.NotNull(view);
        Assert.False(view!.Verified);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreationReadsWhatTheProjectionWrote()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var projection = Projection(dataSource);
        var rider = Guid.NewGuid().ToString("D");

        await projection.HandleAsync(Event(rider, true, 200, "Ada Lovelace"), CancellationToken.None);

        var view = await ReadRiderAsync(dataSource, rider);
        Assert.NotNull(view);
        Assert.True(view!.Verified);
        Assert.Equal("Ada Lovelace", view.Name);
        Assert.Null(await ReadRiderAsync(dataSource, "absent"));
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

        var view = await ReadRiderAsync(dataSource, "load-rider");

        Assert.NotNull(view);
        Assert.True(view!.Verified);
        Assert.Equal("Load rider", view.Name);
    }
}
