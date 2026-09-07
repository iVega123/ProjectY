using System.Diagnostics;
using Google.Protobuf;
using ProjectY.Events;

namespace RentalOperations.Model;

public sealed class RentalEventEnvelope
{
    public string Id { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
    public string? TraceParent { get; set; }

    public static string PartitionKey(Rental rental) => string.IsNullOrEmpty(rental.MotorcycleId)
        ? $"legacy-rental:{rental._id}" : rental.MotorcycleId;

    public static RentalEventEnvelope Create(Rental rental, string topic, DateTime? occurredAt = null)
    {
        var id = $"{rental._id}:{topic}:v1";
        var time = new DateTimeOffset(occurredAt ?? DateTime.UtcNow).ToUnixTimeMilliseconds();
        var message = new RentalEvent
        {
            EventId = id,
            RentalId = rental._id!.Value.ToString(),
            RiderId = rental.UserId,
            MotorcycleId = PartitionKey(rental),
            OccurredAtMs = time,
            PlanDays = (rental.PredictedEndDate - rental.StartDate).Days,
            StartedAtMs = new DateTimeOffset(rental.StartDate.ToUniversalTime()).ToUnixTimeMilliseconds(),
            PredictedEndAtMs = new DateTimeOffset(rental.PredictedEndDate.ToUniversalTime()).ToUnixTimeMilliseconds()
        };
        if (rental.EndDate is { } ended) message.EndedAtMs = new DateTimeOffset(ended.ToUniversalTime()).ToUnixTimeMilliseconds();
        return new() { Id = id, Topic = topic, Payload = message.ToByteArray(), TraceParent = Activity.Current?.Id };
    }
}
