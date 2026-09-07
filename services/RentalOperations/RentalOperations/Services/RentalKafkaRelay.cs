using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using MongoDB.Driver;
using RentalOperations.Data;
using RentalOperations.Model;
using ProjectY.Shared.Messaging;
using ProjectY.Events;

namespace RentalOperations.Services;

/// <summary>Transitional Mongo single-document outbox; SQL migration remains #130.</summary>
public sealed class RentalKafkaRelay(MongoDbContext context, IConfiguration config, ILogger<RentalKafkaRelay> log) : BackgroundService
{
    private static readonly ActivitySource Traces = new(ProjectY.Shared.Observability.MessagingTraceContext.ActivitySourceName);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrap = config["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrap)) return;
        using var registryClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var schemas = new RegisteredEventSchema(registryClient,
            config["Kafka:SchemaRegistryUrl"] ?? throw new InvalidOperationException("Kafka schema registry URL is required."),
            Path.Combine(AppContext.BaseDirectory, "event-contracts"));
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = bootstrap,
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = 5000
        }).Build();
        var rentals = context.Database.GetCollection<Rental>("Rentals");
        var pending = Builders<Rental>.Filter.Ne(r => r.PendingEvents, null) &
                      Builders<Rental>.Filter.SizeGt(r => r.PendingEvents, 0);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BackfillActiveRentalsAsync(rentals, stoppingToken);
                var batch = await rentals.Find(pending).Limit(100).ToListAsync(stoppingToken);
                foreach (var rental in batch)
                    foreach (var item in rental.PendingEvents)
                    {
                        ActivityContext.TryParse(item.TraceParent, null, out var parent);
                        using var span = Traces.StartActivity("publish " + item.Topic, ActivityKind.Producer, parent);
                        span?.SetTag("messaging.system", "kafka");
                        span?.SetTag("messaging.destination.name", item.Topic);
                        var headers = new Headers();
                        RegisteredEventSchema.ValidateKey(RentalEventEnvelope.PartitionKey(rental), RentalEvent.Parser.ParseFrom(item.Payload).MotorcycleId);
                        var schemaId = await schemas.ResolveAsync(item.Topic, stoppingToken);
                        headers.Add("schema-id", Encoding.UTF8.GetBytes(schemaId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        if ((span?.Id ?? item.TraceParent) is { } trace)
                            headers.Add("traceparent", Encoding.UTF8.GetBytes(trace));
                        await producer.ProduceAsync(item.Topic, new Message<string, byte[]>
                        {
                            Key = RentalEventEnvelope.PartitionKey(rental),
                            Value = item.Payload,
                            Headers = headers
                        }, stoppingToken);
                        // A crash after publish replays the same event id. Pull only this
                        // envelope so a concurrent rental close cannot lose its event.
                        await rentals.UpdateOneAsync(r => r._id == rental._id,
                            Builders<Rental>.Update.PullFilter(r => r.PendingEvents, e => e.Id == item.Id),
                            cancellationToken: stoppingToken);
                    }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { log.LogWarning(error, "Kafka relay delayed; rental events retained"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    public static async Task BackfillActiveRentalsAsync(IMongoCollection<Rental> rentals,
        CancellationToken cancellationToken = default)
    {
        // The legacy schema has no PendingEvents field. An empty persisted array
        // means the start was already enqueued/acknowledged and must not be replayed.
        var legacy = Builders<Rental>.Filter.Exists(r => r.PendingEvents, false) &
                     Builders<Rental>.Filter.Eq(r => r.Status, RentalStatus.Active);
        var batch = await rentals.Find(legacy).Limit(100).ToListAsync(cancellationToken);
        foreach (var rental in batch)
        {
            var start = RentalEventEnvelope.Create(rental, "rental.started", rental._id!.Value.CreationTime);
            await rentals.UpdateOneAsync(legacy & Builders<Rental>.Filter.Eq(r => r._id, rental._id),
                Builders<Rental>.Update.Set(r => r.PendingEvents, new List<RentalEventEnvelope> { start }),
                cancellationToken: cancellationToken);
        }
    }
}
