using Confluent.Kafka;
using Npgsql;
using ProjectY.Events;
using RentalOperations.Services.RabbitMQService;
using RentalCore.Errors;

namespace RentalOperations.Services;

/// <summary>
/// What rental-core needs to decide, held locally so the decision costs no call.
/// Rental creation reads it with the rest of its preconditions, in one statement.
/// </summary>
public sealed record RiderView(string RiderId, bool Verified, long VerifiedAtMs, string? Name);

/// <summary>
/// Raised when a rider has not reached the projection yet. This fails closed and
/// says so: the rider may well be entitled, and the client can retry. A generic
/// denial would be indistinguishable from "this rider may not rent", which is a
/// different fact and a permanent one.
/// </summary>
public sealed class RiderProjectionPendingException(string riderId)
    : NotYetAvailableException(
        ProblemTypes.RiderProjectionPending,
        $"Rider {riderId} is awaiting processing. Retry shortly.");

/// <summary>
/// Feeds the rider projection from Kafka. The projection is what lets a rental be
/// created without calling identity, and what lets rental.closed carry a name this
/// service does not own.
/// </summary>
public sealed class RiderProjection(
    NpgsqlDataSource database,
    SqlInboxProcessor inbox,
    IConfiguration config,
    ILogger<RiderProjection> log) : BackgroundService
{
    public const string ConsumerName = "rider-projection";
    public const string Topic = "rider.verified.v2";

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var bootstrap = config["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrap)) return;
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
                    await HandleAsync(message.Message.Value, token);
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

    public async Task HandleAsync(byte[] payload, CancellationToken token)
    {
        var value = RiderEventV2.Parser.ParseFrom(payload);
        if (!value.HasOccurredAtMs || !value.HasVerified || string.IsNullOrWhiteSpace(value.RiderId)
            || string.IsNullOrWhiteSpace(value.EventId))
            throw new InvalidDataException("Missing rider projection fields");

        // The inbox is what makes a redelivery a no-op. Kafka redelivers on any
        // rebalance or uncommitted offset, so this is the normal path, not an edge.
        await inbox.ProcessAsync(value.EventId, ConsumerName, async inner =>
        {
            // No ordering exists between topics, and none is assumed here: the newest
            // fact wins by its own timestamp, so a replayed older event cannot undo it.
            // The WHERE on the upsert is what says so -- an older event matches no row
            // and changes nothing, instead of rolling the projection backwards.
            await using var connection = await database.OpenConnectionAsync(inner);
            await using var command = new NpgsqlCommand("""
                INSERT INTO rider_projection (rider_id, verified, verified_at_ms, rider_name)
                VALUES (@id, @verified, @at, @name)
                ON CONFLICT (rider_id) DO UPDATE
                   SET verified = EXCLUDED.verified,
                       verified_at_ms = EXCLUDED.verified_at_ms,
                       rider_name = EXCLUDED.rider_name
                 WHERE rider_projection.verified_at_ms <= EXCLUDED.verified_at_ms
                """, connection);
            command.Parameters.AddWithValue("id", value.RiderId);
            command.Parameters.AddWithValue("verified", value.Verified);
            command.Parameters.AddWithValue("at", value.OccurredAtMs);
            command.Parameters.AddWithValue("name", value.HasName ? value.Name : DBNull.Value);
            await command.ExecuteNonQueryAsync(inner);
        }, token);
    }
}
