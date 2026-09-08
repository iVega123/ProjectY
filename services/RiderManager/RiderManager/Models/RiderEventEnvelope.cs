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
    public static RiderEventEnvelope VerifiedLegacy(Rider rider) =>
        LegacyFact(rider, IsEntitled(rider.CNHType));

    public static RiderEventEnvelope Verified(Rider rider) =>
        Fact(rider, IsEntitled(rider.CNHType));

    /// <summary>
    /// O piloto deixou de existir, e isso é um fato que precisa viajar.
    ///
    /// rental-core autoriza um aluguel pela projeção local, nunca por uma
    /// chamada a este serviço -- é o ponto do #133, e o que tira a identity do
    /// caminho da requisição. O preço é que apagar a linha aqui não basta: sem
    /// um fato novo, a projeção continua respondendo "verificado" para sempre, e
    /// um piloto apagado segue alugando -- inclusive com credenciais novas, já
    /// que a conta no AuthGate é outra.
    ///
    /// O evento não inventa tópico nem campo. É o mesmo fato com
    /// <c>verified = false</c> e carimbo novo, e o upsert do mais-novo-vence da
    /// projeção derruba a linha sozinho.
    /// </summary>
    public static RiderEventEnvelope RevokedLegacy(Rider rider) => LegacyFact(rider, false);

    public static RiderEventEnvelope Revoked(Rider rider) => Fact(rider, false);

    private static RiderEventEnvelope LegacyFact(Rider rider, bool verified)
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
                Verified = verified,
                OccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }.ToByteArray()
        };
    }

    private static RiderEventEnvelope Fact(Rider rider, bool verified)
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
                Verified = verified,
                OccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }.ToByteArray()
        };
    }
}
