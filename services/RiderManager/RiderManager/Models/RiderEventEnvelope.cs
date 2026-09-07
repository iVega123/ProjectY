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
// rental-core decides whether a rider may rent from its local projection,
    // never from a call to this service. The entitlement rule therefore lives
    // with the owner of the fact: holding an A or AB licence is what "verified"
    // means, and rental-core reads the answer rather than recomputing it.
    public static bool IsEntitled(string? cnhType) =>
        cnhType is "A" or "AB";

    // v1 stays on the wire for consumers that have not moved yet. It cannot
    // carry the name: adding a field to a governed schema is what Apicurio
    // rejects under FULL, which is why v2 exists as its own subject.
    public static RiderEventEnvelope VerifiedLegacy(Rider rider)
    {
        var id = Guid.NewGuid().ToString();
        return new()
        {
            Id = id,
            Topic = "rider.verified",
            RiderId = rider.UserId,
            TraceParent = Activity.Current?.Id,
            Payload = new RiderEvent
            {
                EventId = id,
                RiderId = rider.UserId,
                CnhNumber = rider.CNHNumber,
                Verified = IsEntitled(rider.CNHType),
                OccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }.ToByteArray()
        };
    }

    public static RiderEventEnvelope Verified(Rider rider)
    {
        var id = Guid.NewGuid().ToString();
        return new()
        {
            Id = id,
            Topic = "rider.verified.v2",
            RiderId = rider.UserId,
            TraceParent = Activity.Current?.Id,
            Payload = new RiderEventV2
            {
                EventId = id,
                RiderId = rider.UserId,
                Name = rider.Name,
                CnhNumber = rider.CNHNumber,
                Verified = IsEntitled(rider.CNHType),
                OccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }.ToByteArray()
        };
    }
}
