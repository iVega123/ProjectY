using System.Diagnostics;
using Google.Protobuf;
using ProjectY.Events;

namespace RiderManager.Models;

public sealed class RiderEventEnvelope
{
    public string Id { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string RiderId { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
    public string? TraceParent { get; set; }
    public static RiderEventEnvelope Document(Rider rider, string objectKey)
    {
        var id = Guid.NewGuid().ToString();
        return new()
        {
            Id = id,
            Topic = "document.stored",
            RiderId = rider.UserId,
            TraceParent = Activity.Current?.Id,
            Payload = new RiderEvent
            {
                EventId = id,
                RiderId = rider.UserId,
                ObjectKey = objectKey,
                CnhNumber = rider.CNHNumber,
                OccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }.ToByteArray()
        };
    }
}
