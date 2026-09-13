using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Npgsql;
using RentalCoreTests.Integration;
using RentalOperations.Model;
using RentalOperations.Repository;
using RentalOperations.Services;

namespace RentalCoreTests.Rentals.Integration.Database;

/// <summary>
/// The rental Kafka relay, against the schema that ships.
///
/// Kafka is replaced by a transport that can be switched off, slowed down or
/// killed mid-send. What these tests prove is the database half of the
/// contract: what is claimed, what is marked, and what survives a failure.
/// </summary>
[Collection(RentalCoreDatabaseCollection.Name)]
public sealed class OutboxDispatcherTests(RentalCoreDatabase database)
{
    /// <summary>
    /// The degradation row for Kafka: writes continue, the event waits, and it
    /// leaves once the broker is back. Removing the outbox -- publishing inside
    /// the request -- turns this test red at the first assertion.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Degradation", "kafka")]
    public async Task KafkaDown_TheRentalCommits_TheEventWaits_AndDrainsOnRecovery()
    {
        await database.ResetAsync();
        var motorcycleId = await database.AddMotorcycleAsync("KAF0D01");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var dispatcher = new RentalOutboxDispatcher(dataSource);
        var transport = new FakeTransport { Available = false };
        using var degradations = new DegradationListener();

        var rental = await new SqlRentalRepository(dataSource).CreateRentalAsync(NewRental(motorcycleId));
        var down = await dispatcher.DispatchOnceAsync(transport, CancellationToken.None);

        Assert.NotNull(down.Failure);
        Assert.Equal(0, down.Published);
        Assert.Equal(1, await CountAsync(dataSource, "SELECT count(*) FROM rentals"));
        Assert.Equal(1, await CountAsync(dataSource, "SELECT count(*) FROM outbox WHERE published_at IS NULL"));
        // Released, not parked behind a lease: the next pass may retry at once.
        Assert.Equal(0, await CountAsync(dataSource, "SELECT count(*) FROM outbox WHERE claim_token IS NOT NULL"));
        Assert.True(degradations.Count("kafka") >= 1);

        transport.Available = true;
        var recovered = await dispatcher.DispatchOnceAsync(transport, CancellationToken.None);

        Assert.Null(recovered.Failure);
        Assert.Equal(1, recovered.Published);
        Assert.Equal(0, await CountAsync(dataSource, "SELECT count(*) FROM outbox WHERE published_at IS NULL"));
        Assert.Equal(rental.MotorcycleId.ToString(), Assert.Single(transport.Published).PartitionKey);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#transactional-outbox")]
    public async Task TwoDispatchers_AgainstOneTable_PublishEachRowOnce()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var start = DateTime.UtcNow.AddMinutes(-5);
        for (var index = 0; index < 60; index++)
        {
            await InsertAsync(dataSource, "aggregate-" + index, "rental.started", start.AddMilliseconds(index));
        }

        // Slow enough that both dispatchers are sending at the same time. Without
        // the claim, both select the same batch and every row goes out twice.
        var transport = new FakeTransport { Delay = TimeSpan.FromMilliseconds(15) };
        await Task.WhenAll(DrainAsync(dataSource, transport), DrainAsync(dataSource, transport));

        Assert.Equal(60, transport.Published.Count);
        Assert.Equal(60, transport.Published.Select(item => item.Id).Distinct().Count());
        Assert.Equal(0, await CountAsync(dataSource, "SELECT count(*) FROM outbox WHERE published_at IS NULL"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#transactional-outbox")]
    public async Task EventsOfOneAggregate_LeaveInOrder_EvenWithTwoDispatchers()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var start = DateTime.UtcNow.AddMinutes(-5);
        string[] topics = ["rental.started", "rental.closed", "rental.started", "rental.closed"];
        for (var index = 0; index < topics.Length; index++)
        {
            await InsertAsync(dataSource, "one-motorcycle", topics[index], start.AddSeconds(index));
        }

        var transport = new FakeTransport { Delay = TimeSpan.FromMilliseconds(10) };
        await Task.WhenAll(DrainAsync(dataSource, transport), DrainAsync(dataSource, transport));

        Assert.Equal(topics, transport.Published.Select(item => item.Topic));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#transactional-outbox")]
    public async Task ADispatcherKilledMidSend_DoesNotStrandItsRows()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await InsertAsync(dataSource, "killed", "rental.started", DateTime.UtcNow.AddMinutes(-1));
        var dispatcher = new RentalOutboxDispatcher(dataSource);

        // The process dies while the send is in flight: nothing is marked, nothing
        // is released, and the claim stays on the row.
        using var death = new CancellationTokenSource();
        var hanging = new FakeTransport { OnPublish = death.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchOnceAsync(hanging, death.Token, lease: TimeSpan.FromSeconds(1)));
        Assert.Equal(1, await CountAsync(dataSource, "SELECT count(*) FROM outbox WHERE claim_token IS NOT NULL"));

        var survivor = new FakeTransport();
        var whileLeased = await dispatcher.DispatchOnceAsync(survivor, CancellationToken.None);
        Assert.Equal(0, whileLeased.Claimed);

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var afterLease = await dispatcher.DispatchOnceAsync(survivor, CancellationToken.None);

        Assert.Equal(1, afterLease.Published);
        Assert.Equal(0, await CountAsync(dataSource, "SELECT count(*) FROM outbox WHERE published_at IS NULL"));
    }

    /// <summary>
    /// A batch that outlives its lease. While the first row is being sent, the
    /// other rows' leases lapse and a second dispatcher takes and publishes them.
    /// Without renewing each row before its send, the first dispatcher would send
    /// them again when it got there.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#transactional-outbox")]
    public async Task ABatchThatOutlivesItsLease_DoesNotSendRowsAnotherDispatcherTook()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var start = DateTime.UtcNow.AddMinutes(-5);
        for (var index = 0; index < 3; index++)
        {
            await InsertAsync(dataSource, "slow-batch-" + index, "rental.started", start.AddMilliseconds(index));
        }

        var other = new FakeTransport();
        var lapsed = false;
        var slow = new FakeTransport
        {
            BeforePublish = async sending =>
            {
                if (lapsed) return;
                lapsed = true;
                await using (var expire = dataSource.CreateCommand(
                    "UPDATE outbox SET claimed_until = now() - INTERVAL '1 second' WHERE id <> @id AND published_at IS NULL"))
                {
                    expire.Parameters.AddWithValue("id", sending.Id);
                    await expire.ExecuteNonQueryAsync();
                }
                var taken = await new RentalOutboxDispatcher(dataSource).DispatchOnceAsync(other, CancellationToken.None);
                Assert.Equal(2, taken.Published);
            }
        };

        var pass = await new RentalOutboxDispatcher(dataSource).DispatchOnceAsync(slow, CancellationToken.None);

        Assert.Equal(1, pass.Published);
        var sent = slow.Published.Concat(other.Published).Select(item => item.Id).ToList();
        Assert.Equal(3, sent.Count);
        Assert.Equal(3, sent.Distinct().Count());
        Assert.Equal(0, await CountAsync(dataSource, "SELECT count(*) FROM outbox WHERE published_at IS NULL"));
    }

    /// <summary>
    /// Shutdown in the middle of a batch. The sends Kafka already acknowledged are
    /// marked; only the interrupted row waits for the lease.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#transactional-outbox")]
    public async Task ACancelledBatch_KeepsTheSendsAlreadyAcknowledged()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var start = DateTime.UtcNow.AddMinutes(-5);
        for (var index = 0; index < 3; index++)
        {
            await InsertAsync(dataSource, "shutdown-" + index, "rental.started", start.AddMilliseconds(index));
        }

        using var shutdown = new CancellationTokenSource();
        var calls = 0;
        var transport = new FakeTransport { OnPublish = () => { if (++calls == 3) shutdown.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RentalOutboxDispatcher(dataSource).DispatchOnceAsync(transport, shutdown.Token));

        Assert.Equal(2, transport.Published.Count);
        Assert.Equal(2, await CountAsync(dataSource, "SELECT count(*) FROM outbox WHERE published_at IS NOT NULL"));
    }

    private static async Task DrainAsync(NpgsqlDataSource dataSource, FakeTransport transport)
    {
        var dispatcher = new RentalOutboxDispatcher(dataSource);
        var idle = 0;
        // Idle twice in a row: once can be the other dispatcher still holding the rest.
        while (idle < 3)
        {
            var pass = await dispatcher.DispatchOnceAsync(transport, CancellationToken.None);
            idle = pass.Claimed == 0 ? idle + 1 : 0;
            if (pass.Claimed == 0) await Task.Delay(50);
        }
    }

    private static async Task InsertAsync(NpgsqlDataSource dataSource, string aggregate, string topic, DateTime occurredAt)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO outbox (aggregate_type, aggregate_id, event_type, topic, payload, occurred_at)
            VALUES ('rental', @aggregate, @topic, @topic, @payload, @at)
            """);
        command.Parameters.AddWithValue("aggregate", aggregate);
        command.Parameters.AddWithValue("topic", topic);
        command.Parameters.AddWithValue("payload", new byte[] { 1 });
        command.Parameters.AddWithValue("at", DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Rental NewRental(Guid motorcycleId)
    {
        var start = DateTime.UtcNow.Date.AddDays(1);
        return new Rental
        {
            MotorcycleId = motorcycleId,
            UserId = "rider-kafka-down",
            RiderName = "Ada Lovelace",
            MotorcycleLicencePlate = "KAF0D01",
            StartDate = start,
            PredictedEndDate = start.AddDays(7),
            InitCost = 210m
        };
    }

    private sealed class FakeTransport : IRentalEventTransport
    {
        public volatile bool Available = true;
        public TimeSpan Delay { get; init; }
        public Action? OnPublish { get; init; }
        public Func<PendingRentalEvent, Task>? BeforePublish { get; init; }
        public ConcurrentQueue<PendingRentalEvent> PublishedQueue { get; } = new();
        public IReadOnlyList<PendingRentalEvent> Published => PublishedQueue.ToList();

        public async Task PublishAsync(PendingRentalEvent pending, CancellationToken token)
        {
            OnPublish?.Invoke();
            if (BeforePublish is not null) await BeforePublish(pending);
            token.ThrowIfCancellationRequested();
            if (!Available) throw new InvalidOperationException("Broker unavailable");
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, token);
            PublishedQueue.Enqueue(pending);
        }
    }

    private sealed class DegradationListener : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentDictionary<string, long> counts = new();

        public DegradationListener()
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == Degradation.MeterName && instrument.Name == Degradation.InstrumentName)
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "dependency" && tag.Value is string dependency)
                        counts.AddOrUpdate(dependency, value, (_, current) => current + value);
            });
            listener.Start();
        }

        public long Count(string dependency) => counts.GetValueOrDefault(dependency);

        public void Dispose() => listener.Dispose();
    }
}
