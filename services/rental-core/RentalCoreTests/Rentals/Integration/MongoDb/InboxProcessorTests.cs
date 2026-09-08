using MongoDB.Bson;
using MongoDB.Driver;
using RentalOperations.Data;
using RentalOperations.Model;
using RentalOperations.Services.RabbitMQService;
using Testcontainers.MongoDb;

namespace RentalOperationsTests.Integration.MongoDb;

public sealed class InboxProcessorTests : IAsyncLifetime
{
    private const string DatabaseName = "rental_inbox_tests";
    private readonly MongoDbContainer _database = new MongoDbBuilder("mongo:8.0").Build();
    private MongoDbContext _context = null!;
    private MongoInboxOptions _options = null!;

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
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#mongo-inbox-convergence")]
    public async Task SameMessageDeliveredTwice_ExecutesHandlerOnce()
    {
        var processor = new MongoInboxProcessor(_context, _options, TimeProvider.System);
        var effects = _context.Database.GetCollection<BsonDocument>("Effects");
        var messageId = Guid.NewGuid().ToString("D");

        async Task Effect(CancellationToken cancellationToken) => await effects.InsertOneAsync(
            new BsonDocument("messageId", messageId),
            cancellationToken: cancellationToken);

        Assert.True(await processor.ProcessAsync(messageId, "test-consumer", Effect));
        Assert.False(await processor.ProcessAsync(messageId, "test-consumer", Effect));
        Assert.Equal(1, await effects.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#mongo-inbox-convergence")]
    public async Task CrashAfterIdempotentEffect_RedeliveryConvergesAndCompletesInbox()
    {
        var processor = new MongoInboxProcessor(_context, _options, TimeProvider.System);
        var rentals = _context.Database.GetCollection<BsonDocument>("Rentals");
        await rentals.InsertOneAsync(new BsonDocument
        {
            ["MotorcycleLicencePlate"] = "OLD-0001"
        });
        var firstAttempt = true;

        async Task IdempotentEffect(CancellationToken cancellationToken)
        {
            await rentals.UpdateManyAsync(
                Builders<BsonDocument>.Filter.Eq("MotorcycleLicencePlate", "OLD-0001"),
                Builders<BsonDocument>.Update.Set("MotorcycleLicencePlate", "NEW-0001"),
                cancellationToken: cancellationToken);
            if (firstAttempt)
            {
                firstAttempt = false;
                throw new InvalidOperationException("simulated crash before acknowledgement");
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(
            "licence-message",
            "rental-operations/licence-update/v1",
            IdempotentEffect));
        Assert.True(await processor.ProcessAsync(
            "licence-message",
            "rental-operations/licence-update/v1",
            IdempotentEffect));

        Assert.Equal(1, await rentals.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("MotorcycleLicencePlate", "NEW-0001")));
        var inbox = await _context.Database.GetCollection<InboxMessage>("InboxMessages")
            .Find(message => message.MessageId == "licence-message")
            .SingleAsync();
        Assert.Equal(MongoInboxProcessor.CompletedStatus, inbox.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#retention-boundaries")]
    public async Task Initializer_SchedulesRetentionWithTtlIndex()
    {
        var messages = _context.Database.GetCollection<InboxMessage>("InboxMessages");
        var indexes = await (await messages.Indexes.ListAsync()).ToListAsync();
        var retention = Assert.Single(indexes, index =>
            index["name"] == MongoInboxInitializer.RetentionIndexName);

        Assert.Equal(_options.RetentionPeriod.TotalSeconds, retention["expireAfterSeconds"].ToDouble());
    }
}
