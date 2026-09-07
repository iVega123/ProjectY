using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProjectY.Shared.Messaging;

/// <summary>Resolve once per topic. Kafka payloads remain locally decodable Protobuf.</summary>
public sealed class RegisteredEventSchema(HttpClient client, string registryUrl, string contractsDirectory)
{
    private readonly ConcurrentDictionary<string, int> _ids = new();
    private readonly SemaphoreSlim _resolve = new(1, 1);

    public async Task<int> ResolveAsync(string topic, CancellationToken token)
    {
        if (_ids.TryGetValue(topic, out var cached)) return cached;
        await _resolve.WaitAsync(token);
        try
        {
            if (_ids.TryGetValue(topic, out cached)) return cached;
            using var topics = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(contractsDirectory, "topics.json"), token));
            var schemaFile = topics.RootElement.GetProperty(topic).GetProperty("schema").GetString()!;
            var schema = await File.ReadAllTextAsync(Path.Combine(contractsDirectory, "events", schemaFile), token);
            // Lookup only: deployment registers schemas under FULL before rollout.
            using var response = await client.PostAsJsonAsync(
                registryUrl.TrimEnd('/') + "/subjects/" + Uri.EscapeDataString(topic + "-value"),
                new { schemaType = "PROTOBUF", schema }, token);
            response.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var id = result.RootElement.GetProperty("id").GetInt32();
            if (id <= 0) throw new InvalidDataException("Registry returned an invalid schema ID.");
            _ids[topic] = id;
            return id;
        }
        finally { _resolve.Release(); }
    }

    public static void ValidateKey(string key, string eventKey)
    {
        if (string.IsNullOrWhiteSpace(eventKey) || !string.Equals(key, eventKey, StringComparison.Ordinal))
            throw new InvalidDataException("Kafka key does not match the immutable event identifier.");
    }
}
