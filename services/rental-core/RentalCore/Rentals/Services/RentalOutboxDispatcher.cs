using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Npgsql;
using NpgsqlTypes;
using ProjectY.Events;
using ProjectY.Shared.Messaging;

namespace RentalOperations.Services;

public sealed record PendingRentalEvent(Guid Id, string PartitionKey, string Topic, byte[] Payload, string? TraceParent);

/// <summary>Where a claimed rental event goes. Kafka in production; a fake in tests.</summary>
public interface IRentalEventTransport
{
    Task PublishAsync(PendingRentalEvent pending, CancellationToken token);
}

/// <summary>What one pass did. <see cref="Failure"/> is the transport error that stopped it, if any.</summary>
public readonly record struct RelayPass(int Claimed, int Published, Exception? Failure);

/// <summary>
/// Claims rental events from the outbox, hands them to a transport and marks them published.
///
/// Three guarantees, each with a test in OutboxDispatcherTests:
///
/// - Two dispatchers against one table publish each row once. The claim is an
///   UPDATE over <c>FOR UPDATE SKIP LOCKED</c>, so the second dispatcher skips
///   what the first holds instead of waiting for it or sending it again.
/// - Events of one aggregate leave in order. A row is claimable only when no
///   earlier row of the same aggregate is still pending, claimed or not.
/// - A dispatcher that dies mid-send does not strand its rows. The claim is a
///   lease; when it expires, the next pass takes the rows.
///
/// A transport failure stops the pass, releases what was not sent and counts a
/// degradation. The rental that wrote the row has already committed: Kafka
/// being down delays the event, it does not refuse the write.
/// </summary>
public sealed class RentalOutboxDispatcher(NpgsqlDataSource database)
{
    public const int BatchSize = 100;
    public static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(30);

    private static readonly Meter Meter = new(Degradation.MeterName);
    private static long pendingCount;
    private static double oldestPendingSeconds;
    private static readonly ObservableGauge<long> Pending = Meter.CreateObservableGauge(
        "projecty.rental.outbox.pending", () => Volatile.Read(ref pendingCount));
    private static readonly ObservableGauge<double> OldestPending = Meter.CreateObservableGauge(
        "projecty.rental.outbox.oldest_age_seconds", () => Volatile.Read(ref oldestPendingSeconds));

    public async Task<RelayPass> DispatchOnceAsync(
        IRentalEventTransport transport,
        CancellationToken token,
        TimeSpan? lease = null)
    {
        var claimToken = Guid.NewGuid();
        var claimed = await ClaimAsync(claimToken, lease ?? ClaimLease, token);
        var published = new List<Guid>(claimed.Count);
        Exception? failure = null;
        foreach (var pending in claimed)
        {
            try
            {
                await transport.PublishAsync(pending, token);
                published.Add(pending.Id);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                failure = error;
                break;
            }
        }

        // Not cancellable on purpose: an event already on the topic but left
        // unmarked is published again after the lease, which is a duplicate the
        // shutdown could have avoided.
        if (published.Count > 0) await MarkPublishedAsync(published, claimToken, CancellationToken.None);
        if (failure is not null)
        {
            await ReleaseAsync(claimToken, CancellationToken.None);
            Degradation.Record("kafka", "event-propagation");
        }

        await MeasureBacklogAsync(CancellationToken.None);
        return new RelayPass(claimed.Count, published.Count, failure);
    }

    private async Task<List<PendingRentalEvent>> ClaimAsync(Guid claimToken, TimeSpan lease, CancellationToken token)
    {
        var claimed = new List<PendingRentalEvent>();
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            WITH candidates AS (
                SELECT candidate.id
                  FROM outbox AS candidate
                 WHERE candidate.published_at IS NULL
                   AND candidate.aggregate_type = 'rental'
                   AND (candidate.claimed_until IS NULL OR candidate.claimed_until < now())
                   AND NOT EXISTS (
                        SELECT 1 FROM outbox AS earlier
                         WHERE earlier.aggregate_type = candidate.aggregate_type
                           AND earlier.aggregate_id = candidate.aggregate_id
                           AND earlier.published_at IS NULL
                           AND earlier.occurred_at < candidate.occurred_at)
                 ORDER BY candidate.occurred_at
                 LIMIT @limit
                 FOR UPDATE SKIP LOCKED)
            UPDATE outbox
               SET claim_token = @claim,
                   claimed_until = now() + @lease
              FROM candidates
             WHERE outbox.id = candidates.id
            RETURNING outbox.id, outbox.aggregate_id, outbox.topic, outbox.payload, outbox.trace_parent, outbox.occurred_at
            """, connection);
        command.Parameters.AddWithValue("limit", BatchSize);
        command.Parameters.AddWithValue("claim", claimToken);
        command.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.Interval) { Value = lease });
        var rows = new List<(PendingRentalEvent Event, DateTime OccurredAt)>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                rows.Add((new PendingRentalEvent(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    (byte[])reader[3],
                    reader.IsDBNull(4) ? null : reader.GetString(4)), reader.GetDateTime(5)));
            }
        }

        // RETURNING does not promise the CTE's order; the send order is restored here.
        claimed.AddRange(rows.OrderBy(row => row.OccurredAt).Select(row => row.Event));
        return claimed;
    }

    private async Task MarkPublishedAsync(List<Guid> ids, Guid claimToken, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            UPDATE outbox
               SET published_at = now(), claim_token = NULL, claimed_until = NULL
             WHERE id = ANY(@ids) AND claim_token = @claim AND published_at IS NULL
            """, connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        command.Parameters.AddWithValue("claim", claimToken);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task ReleaseAsync(Guid claimToken, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            UPDATE outbox
               SET claim_token = NULL, claimed_until = NULL
             WHERE claim_token = @claim AND published_at IS NULL
            """, connection);
        command.Parameters.AddWithValue("claim", claimToken);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task MeasureBacklogAsync(CancellationToken token)
    {
        try
        {
            await using var connection = await database.OpenConnectionAsync(token);
            await using var command = new NpgsqlCommand("""
                SELECT count(*), COALESCE(EXTRACT(EPOCH FROM now() - min(occurred_at)), 0)::FLOAT8
                  FROM outbox
                 WHERE published_at IS NULL AND aggregate_type = 'rental'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return;
            Volatile.Write(ref pendingCount, reader.GetInt64(0));
            Volatile.Write(ref oldestPendingSeconds, Math.Max(0, reader.GetDouble(1)));
        }
        catch (NpgsqlException)
        {
            // The backlog gauge is an observation; failing to read it must not fail the pass.
        }
    }
}

/// <summary>Publishes a rental event to Kafka with its schema id and trace context.</summary>
public sealed class KafkaRentalEventTransport : IRentalEventTransport, IDisposable
{
    private static readonly ActivitySource Traces =
        new(ProjectY.Shared.Observability.MessagingTraceContext.ActivitySourceName);

    private readonly HttpClient registryClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly RegisteredEventSchema schemas;
    private readonly IProducer<string, byte[]> producer;

    public KafkaRentalEventTransport(string bootstrapServers, string schemaRegistryUrl, string contractsDirectory)
    {
        schemas = new RegisteredEventSchema(registryClient, schemaRegistryUrl, contractsDirectory);
        producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = 5000
        }).Build();
    }

    public async Task PublishAsync(PendingRentalEvent pending, CancellationToken token)
    {
        ActivityContext.TryParse(pending.TraceParent, null, out var parent);
        using var span = Traces.StartActivity("publish " + pending.Topic, ActivityKind.Producer, parent);
        span?.SetTag("messaging.system", "kafka");
        span?.SetTag("messaging.destination.name", pending.Topic);
        RegisteredEventSchema.ValidateKey(
            pending.PartitionKey, RentalEvent.Parser.ParseFrom(pending.Payload).MotorcycleId);
        var schemaId = await schemas.ResolveAsync(pending.Topic, token);
        var headers = new Headers
        {
            { "schema-id", Encoding.UTF8.GetBytes(schemaId.ToString(CultureInfo.InvariantCulture)) }
        };
        if ((span?.Id ?? pending.TraceParent) is { } trace)
            headers.Add("traceparent", Encoding.UTF8.GetBytes(trace));
        await producer.ProduceAsync(pending.Topic, new Message<string, byte[]>
        {
            Key = pending.PartitionKey,
            Value = pending.Payload,
            Headers = headers
        }, token);
    }

    public void Dispose()
    {
        producer.Dispose();
        registryClient.Dispose();
    }
}
