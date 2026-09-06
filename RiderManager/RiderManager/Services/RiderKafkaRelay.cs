using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using RiderManager.Data;
using System.Diagnostics;
using System.Text;
using ProjectY.Shared.Observability;

namespace RiderManager.Services;

public sealed class RiderKafkaRelay(IServiceScopeFactory scopes, IConfiguration config, ILogger<RiderKafkaRelay> log) : BackgroundService
{
    private static readonly ActivitySource Traces = new(MessagingTraceContext.ActivitySourceName);
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(config["Kafka:BootstrapServers"])) return;
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        { BootstrapServers = config["Kafka:BootstrapServers"], EnableIdempotence = true, Acks = Acks.All, MessageTimeoutMs = 5000 }).Build();
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                foreach (var item in await db.EventOutbox.Take(100).ToListAsync(token))
                {
                    ActivityContext.TryParse(item.TraceParent, null, out var parent);
                    using var span = Traces.StartActivity("publish document.stored", ActivityKind.Producer, parent);
                    span?.SetTag("messaging.system", "kafka");
                    var headers = new Headers();
                    if ((span?.Id ?? item.TraceParent) is { } trace) headers.Add("traceparent", Encoding.UTF8.GetBytes(trace));
                    await producer.ProduceAsync(item.Topic, new Message<string, byte[]> { Key = item.RiderId, Value = item.Payload, Headers = headers }, token);
                    db.EventOutbox.Remove(item);
                    await db.SaveChangesAsync(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { log.LogWarning(error, "Document event retained for Kafka retry"); }
            await Task.Delay(2000, token);
        }
    }
}
