using Confluent.Kafka;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using ProjectY.Events;
using RentalOperations.Data;
using RentalOperations.Services.RabbitMQService;

namespace RentalOperations.Services;

/// <summary>What rental-core needs to decide, held locally so the decision costs no call.</summary>
public sealed record RiderView(string RiderId, bool Verified, long VerifiedAtMs, string? Name);

public interface IRiderProjectionStore
{
    Task<RiderView?> GetAsync(string riderId, CancellationToken token);
}

public sealed class RiderSnapshot
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public long VerifiedAtMs { get; set; }
    public string? Name { get; set; }
}

/// <summary>
/// Raised when a rider has not reached the projection yet. This fails closed and
/// says so: the rider may well be entitled, and the client can retry. A generic
/// denial would be indistinguishable from "this rider may not rent", which is a
/// different fact and a permanent one.
/// </summary>
public sealed class RiderProjectionPendingException(string riderId)
    : Exception($"Rider {riderId} is awaiting processing.");

public sealed class MongoRiderProjectionStore(MongoDbContext db) : IRiderProjectionStore
{
    public const string CollectionName = "RiderProjection";

    public async Task<RiderView?> GetAsync(string riderId, CancellationToken token)
    {
        var snapshot = await db.Database.GetCollection<RiderSnapshot>(CollectionName)
            .Find(row => row.Id == riderId).FirstOrDefaultAsync(token);
        return snapshot is null ? null
            : new RiderView(snapshot.Id, snapshot.Verified, snapshot.VerifiedAtMs, snapshot.Name);
    }
}

/// <summary>
/// Feeds the rider projection from Kafka. The projection is what lets a rental be
/// created without calling identity, and what lets rental.closed carry a name this
/// service does not own.
/// </summary>
public sealed class RiderProjection(
    MongoDbContext db,
    MongoInboxProcessor inbox,
    IConfiguration config,
    ILogger<RiderProjection> log) : BackgroundService
{
    public const string ConsumerName = "rider-projection";
    public const string Topic = "rider.verified.v2";

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var bootstrap = config["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrap)) return;
        var riders = db.Database.GetCollection<RiderSnapshot>(MongoRiderProjectionStore.CollectionName);
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = "rental-rider-projection-v1",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe(Topic);
        try
        {
            while (!token.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? message = null;
                try
                {
                    message = consumer.Consume(TimeSpan.FromSeconds(1));
                    if (message is null) continue;
                    await HandleAsync(riders, message.Message.Value, token);
                    consumer.Commit(message);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    log.LogWarning(error, "Rider projection delayed; last values retained");
                    if (message is not null) consumer.Seek(message.TopicPartitionOffset);
                    await Task.Delay(2000, token);
                }
            }
        }
        finally { consumer.Close(); }
    }

    public async Task HandleAsync(IMongoCollection<RiderSnapshot> riders, byte[] payload, CancellationToken token)
    {
        var value = RiderEventV2.Parser.ParseFrom(payload);
        if (!value.HasOccurredAtMs || !value.HasVerified || string.IsNullOrWhiteSpace(value.RiderId)
            || string.IsNullOrWhiteSpace(value.EventId))
            throw new InvalidDataException("Missing rider projection fields");

        // The inbox is what makes a redelivery a no-op. Kafka redelivers on any
        // rebalance or uncommitted offset, so this is the normal path, not an edge.
        await inbox.ProcessAsync(value.EventId, ConsumerName, async _ =>
        {
            // No ordering exists between topics, and none is assumed here: the newest
            // fact wins by its own timestamp, so a replayed older event cannot undo it.
            try
            {
                await riders.UpdateOneAsync(
                    row => row.Id == value.RiderId && row.VerifiedAtMs <= value.OccurredAtMs,
                    Builders<RiderSnapshot>.Update
                        .SetOnInsert(row => row.Id, value.RiderId)
                        .Set(row => row.Verified, value.Verified)
                        .Set(row => row.VerifiedAtMs, value.OccurredAtMs)
                        .Set(row => row.Name, value.HasName ? value.Name : null),
                    new UpdateOptions { IsUpsert = true },
                    token);
            }
            catch (MongoWriteException duplicate)
                when (duplicate.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // The filter missed because a newer fact is already stored, so the
                // upsert tried to insert a second row for this rider. Nothing to do:
                // an older event must not roll the projection backwards.
            }
        }, token);
    }
}
