using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using MongoDB.Driver;
using ProjectY.Events;
using RentalOperations.Data;

namespace RentalOperations.Services;

public sealed record PriceTier([property: JsonPropertyName("max_days")] int MaxDays,
    [property: JsonPropertyName("daily_minor")] long DailyMinor);
public sealed record PriceTable([property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("tiers")] PriceTier[] Tiers);

public static class LocalPricing
{
    private static readonly object Gate = new();
    private static long tableAt;
    public static readonly ConcurrentDictionary<string, (int Score, long At)> Scores = new();
    private static readonly Meter Meter = new("ProjectY.Resilience");
    private static readonly ObservableGauge<double> ScoreAge = Meter.CreateObservableGauge(
        "projecty.risk.projection.oldest_score_age_seconds", () => Scores.IsEmpty ? 0d :
            Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Scores.Values.Min(s => s.At)) / 1000d));
    private static PriceTable current = JsonSerializer.Deserialize<PriceTable>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pricing-policy.json")))!;
    public static decimal DailyRate(int days) => Volatile.Read(ref current).Tiers.First(t => days <= t.MaxDays).DailyMinor / 100m;
    public static decimal DailyRate(int days, string rider) => decimal.Round(
        DailyRate(days) * (Scores.TryGetValue(rider, out var risk) && risk.Score <= 30 ? 0.95m : 1m),
        2, MidpointRounding.AwayFromZero);
    public static void ApplyScore(string rider, int score, long at)
    {
        if (string.IsNullOrWhiteSpace(rider) || score is < 0 or > 100 || at <= 0)
            throw new InvalidDataException("Invalid risk score");
        Scores.AddOrUpdate(rider, (score, at), (_, old) => at >= old.At ? (score, at) : old);
    }
    public static void Apply(PriceTable table, long at)
    {
        if (table is null || string.IsNullOrWhiteSpace(table.Version) || at <= 0 || table.Tiers is null
            || table.Tiers.Any(t => t.DailyMinor is <= 0 or > 100000)
            || !table.Tiers.Select(t => t.MaxDays).SequenceEqual(new[] { 7, 15, 30, 45, 36500 }))
            throw new InvalidDataException("Invalid pricing table");
        lock (Gate)
        {
            if (at < tableAt) return;
            Volatile.Write(ref current, table);
            tableAt = at;
        }
    }
}

public sealed class PricingProjection(MongoDbContext db, IConfiguration config, ILogger<PricingProjection> log) : BackgroundService
{
    public sealed class Snapshot
    {
        public string Id { get; set; } = string.Empty;
        public byte[] Payload { get; set; } = [];
        public long At { get; set; }
        public string Topic { get; set; } = string.Empty;
    }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var bootstrap = config["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrap)) return;
        var collection = db.Database.GetCollection<Snapshot>("RiskPricingProjection");
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        { BootstrapServers = bootstrap, GroupId = "rental-pricing-v1-" + Environment.MachineName, EnableAutoCommit = false, AutoOffsetReset = AutoOffsetReset.Earliest }).Build();
        // Rehydrate before consumption. Request handling retains the packaged conservative table meanwhile.
        while (!token.IsCancellationRequested)
        {
            try { foreach (var row in await collection.Find(FilterDefinition<Snapshot>.Empty).ToListAsync(token)) Apply(row.Topic, RiderEvent.Parser.ParseFrom(row.Payload)); break; }
            catch (Exception error) { log.LogWarning(error, "Pricing rehydration delayed"); await Task.Delay(2000, token); }
        }
        consumer.Subscribe(["risk.scored", "pricing.updated"]);
        try
        {
            while (!token.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? message = null;
                try
                {
                    message = consumer.Consume(TimeSpan.FromSeconds(1));
                    if (message is null) continue;
                    var value = RiderEvent.Parser.ParseFrom(message.Message.Value);
                    var id = message.Topic == "pricing.updated" ? "pricing" : "rider:" + value.RiderId;
                    var previous = await collection.Find(r => r.Id == id).FirstOrDefaultAsync(token);
                    if (previous is null || value.OccurredAtMs >= previous.At)
                    {
                        await collection.ReplaceOneAsync(r => r.Id == id && r.At <= value.OccurredAtMs, new Snapshot { Id = id, At = value.OccurredAtMs, Payload = message.Message.Value, Topic = message.Topic }, new ReplaceOptions { IsUpsert = true }, token);
                    }
                    // Read the persisted winner, including when another replica advanced it.
                    var winner = await collection.Find(r => r.Id == id).FirstAsync(token);
                    Apply(winner.Topic, RiderEvent.Parser.ParseFrom(winner.Payload));
                    consumer.Commit(message);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    log.LogWarning(error, "Risk/pricing projection delayed; last values retained");
                    if (message is not null) consumer.Seek(message.TopicPartitionOffset);
                    await Task.Delay(2000, token);
                }
            }
        }
        finally { consumer.Close(); }
    }
    private static void Apply(string topic, RiderEvent value)
    {
        if (!value.HasOccurredAtMs || (topic == "risk.scored" && !value.HasRiskScore))
            throw new InvalidDataException("Missing projection fields");
        if (topic == "pricing.updated") LocalPricing.Apply(JsonSerializer.Deserialize<PriceTable>(value.PricingJson)!, value.OccurredAtMs);
        else LocalPricing.ApplyScore(value.RiderId, value.RiskScore, value.OccurredAtMs);
    }
}
