using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using ProjectY.Events;
using RentalOperations.Data;
using RentalOperations.Model;
using RentalOperations.Services;
using RentalOperations.Services.RabbitMQService;
using Testcontainers.MongoDb;

namespace RentalOperationsTests.Integration.MongoDb;

public sealed class RiderProjectionTests : IAsyncLifetime
{
    private const string DatabaseName = "rental_rider_projection_tests";
    private readonly MongoDbContainer _database = new MongoDbBuilder("mongo:8.0").Build();
    private MongoDbContext _context = null!;
    private MongoInboxOptions _options = null!;
    private RiderProjection _projection = null!;
    private IMongoCollection<RiderSnapshot> _riders = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _context = new MongoDbContext(_database.GetConnectionString(), DatabaseName);
        _options = new MongoInboxOptions
        {
            ClaimLease = TimeSpan.FromSeconds(5),
            RetentionPeriod = TimeSpan.FromDays(7)
        };
        await new MongoInboxInitializer(_context, _options).StartAsync(CancellationToken.None);
        _projection = new RiderProjection(
            _context,
            new MongoInboxProcessor(_context, _options, TimeProvider.System),
            new ConfigurationBuilder().Build(),
            NullLogger<RiderProjection>.Instance);
        _riders = _context.Database.GetCollection<RiderSnapshot>(MongoRiderProjectionStore.CollectionName);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    private static byte[] Event(string riderId, bool verified, long at, string name, string? eventId = null) =>
        new RiderEventV2
        {
            EventId = eventId ?? Guid.NewGuid().ToString("D"),
            RiderId = riderId,
            OccurredAtMs = at,
            Verified = verified,
            Name = name
        }.ToByteArray();

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#mongo-inbox-convergence")]
    public async Task ReplayedVerificationChangesNothing()
    {
        var rider = Guid.NewGuid().ToString("D");
        var payload = Event(rider, true, 200, "Ada Lovelace");

        await _projection.HandleAsync(_riders, payload, CancellationToken.None);
        await _projection.HandleAsync(_riders, payload, CancellationToken.None);

        var stored = await _riders.Find(row => row.Id == rider).ToListAsync();
        var single = Assert.Single(stored);
        Assert.True(single.Verified);
        Assert.Equal(200, single.VerifiedAtMs);
        Assert.Equal("Ada Lovelace", single.Name);

        // The inbox is the evidence: one completed row, so the second delivery was
        // recognised rather than merely landing on an identical value.
        var inbox = await _context.Database.GetCollection<InboxMessage>("InboxMessages")
            .Find(message => message.ConsumerName == RiderProjection.ConsumerName).ToListAsync();
        Assert.Equal(MongoInboxProcessor.CompletedStatus, Assert.Single(inbox).Status);
    }

    // Topics carry no ordering relative to each other, and a partition redelivers on
    // rebalance. An older verification arriving late must not un-verify a rider.
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0015#carried-state")]
    public async Task OlderVerificationDoesNotRollTheProjectionBackwards()
    {
        var rider = Guid.NewGuid().ToString("D");

        await _projection.HandleAsync(_riders, Event(rider, true, 200, "Ada Lovelace"), CancellationToken.None);
        await _projection.HandleAsync(_riders, Event(rider, false, 100, "Stale Name"), CancellationToken.None);

        var stored = Assert.Single(await _riders.Find(row => row.Id == rider).ToListAsync());
        Assert.True(stored.Verified);
        Assert.Equal(200, stored.VerifiedAtMs);
        Assert.Equal("Ada Lovelace", stored.Name);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NewerVerificationAdvancesTheProjection()
    {
        var rider = Guid.NewGuid().ToString("D");

        await _projection.HandleAsync(_riders, Event(rider, true, 100, "Ada Lovelace"), CancellationToken.None);
        await _projection.HandleAsync(_riders, Event(rider, false, 300, "Ada Byron"), CancellationToken.None);

        var stored = Assert.Single(await _riders.Find(row => row.Id == rider).ToListAsync());
        Assert.False(stored.Verified);
        Assert.Equal(300, stored.VerifiedAtMs);
        Assert.Equal("Ada Byron", stored.Name);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StoreReadsWhatTheProjectionWrote()
    {
        var rider = Guid.NewGuid().ToString("D");
        await _projection.HandleAsync(_riders, Event(rider, true, 200, "Ada Lovelace"), CancellationToken.None);

        var view = await new MongoRiderProjectionStore(_context).GetAsync(rider, CancellationToken.None);

        Assert.NotNull(view);
        Assert.True(view!.Verified);
        Assert.Equal("Ada Lovelace", view.Name);
        Assert.Null(await new MongoRiderProjectionStore(_context).GetAsync("absent", CancellationToken.None));
    }
}
