using System.Diagnostics;
using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Npgsql;
using ProjectY.Events;
using ProjectY.Shared.Messaging;

namespace RentalOperations.Services;

/// <summary>
/// Publica no Kafka o que a transação do aluguel deixou no outbox.
///
/// Marcar como publicado é um UPDATE separado, depois do ProduceAsync, e a
/// ordem é essa de propósito: uma queda entre os dois republica o mesmo evento,
/// e o id do evento -- derivado do aluguel e do assunto -- faz o inbox do outro
/// lado reconhecê-lo. O contrário (marcar antes) perderia o evento em silêncio,
/// que é a falha que o outbox existe para impedir.
/// </summary>
public sealed class RentalKafkaRelay(
    NpgsqlDataSource database,
    IConfiguration config,
    ILogger<RentalKafkaRelay> log) : BackgroundService
{
    private static readonly ActivitySource Traces =
        new(ProjectY.Shared.Observability.MessagingTraceContext.ActivitySourceName);

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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var pending in await PendingAsync(stoppingToken))
                {
                    ActivityContext.TryParse(pending.TraceParent, null, out var parent);
                    using var span = Traces.StartActivity("publish " + pending.Topic, ActivityKind.Producer, parent);
                    span?.SetTag("messaging.system", "kafka");
                    span?.SetTag("messaging.destination.name", pending.Topic);
                    var headers = new Headers();
                    RegisteredEventSchema.ValidateKey(
                        pending.PartitionKey, RentalEvent.Parser.ParseFrom(pending.Payload).MotorcycleId);
                    var schemaId = await schemas.ResolveAsync(pending.Topic, stoppingToken);
                    headers.Add("schema-id", Encoding.UTF8.GetBytes(schemaId.ToString(CultureInfo.InvariantCulture)));
                    if ((span?.Id ?? pending.TraceParent) is { } trace)
                        headers.Add("traceparent", Encoding.UTF8.GetBytes(trace));
                    await producer.ProduceAsync(pending.Topic, new Message<string, byte[]>
                    {
                        Key = pending.PartitionKey,
                        Value = pending.Payload,
                        Headers = headers
                    }, stoppingToken);
                    await MarkPublishedAsync(pending.Id, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { log.LogWarning(error, "Kafka relay delayed; rental events retained"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    public sealed record PendingEvent(Guid Id, string PartitionKey, string Topic, byte[] Payload, string? TraceParent);

    private async Task<List<PendingEvent>> PendingAsync(CancellationToken token)
    {
        var pending = new List<PendingEvent>();
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT id, aggregate_id, topic, payload, trace_parent
              FROM outbox
             WHERE published_at IS NULL
               AND aggregate_type = 'rental'
             ORDER BY occurred_at
             LIMIT 100
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            pending.Add(new PendingEvent(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                (byte[])reader[3],
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return pending;
    }

    private async Task MarkPublishedAsync(Guid id, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(
            "UPDATE outbox SET published_at = now() WHERE id = @id AND published_at IS NULL", connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(token);
    }
}
