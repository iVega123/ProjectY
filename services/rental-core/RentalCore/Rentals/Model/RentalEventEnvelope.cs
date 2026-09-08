using System.Diagnostics;
using Google.Protobuf;
using ProjectY.Events;

namespace RentalOperations.Model;

/// <summary>
/// Um evento de aluguel pronto para virar linha do outbox.
///
/// O identificador é derivado do aluguel e do assunto, e não sorteado: uma
/// republicação depois de uma falha precisa carregar o mesmo id, ou o inbox do
/// outro lado não teria como reconhecê-la como repetida.
/// </summary>
public sealed record RentalEventEnvelope(string Id, string Topic, byte[] Payload, string? TraceParent)
{
    public static string PartitionKey(Rental rental) => rental.MotorcycleId.ToString();

    public static RentalEventEnvelope Create(Rental rental, string topic, DateTime? occurredAt = null)
    {
        var id = $"{rental.Id}:{topic}:v1";
        var time = new DateTimeOffset(occurredAt ?? DateTime.UtcNow).ToUnixTimeMilliseconds();
        var message = new RentalEvent
        {
            EventId = id,
            RentalId = rental.Id.ToString(),
            RiderId = rental.UserId,
            MotorcycleId = PartitionKey(rental),
            OccurredAtMs = time,
            PlanDays = (rental.PredictedEndDate - rental.StartDate).Days,
            AgreedTotalMinor = checked((long)decimal.Round(rental.InitCost * 100m, 0, MidpointRounding.ToEven)),
            Currency = "BRL",
            StartedAtMs = new DateTimeOffset(rental.StartDate.ToUniversalTime()).ToUnixTimeMilliseconds(),
            PredictedEndAtMs = new DateTimeOffset(rental.PredictedEndDate.ToUniversalTime()).ToUnixTimeMilliseconds()
        };
        if (rental.RiderName is { } riderName) message.RiderName = riderName;
        if (rental.EndDate is { } ended) message.EndedAtMs = new DateTimeOffset(ended.ToUniversalTime()).ToUnixTimeMilliseconds();
        return new RentalEventEnvelope(id, topic, message.ToByteArray(), Activity.Current?.Id);
    }
}
