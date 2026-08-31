using MongoDB.Bson.Serialization.Attributes;

namespace RentalOperations.Model;

public sealed class InboxMessage
{
    [BsonId]
    public required string Id { get; set; }
    public required string MessageId { get; set; }
    public required string ConsumerName { get; set; }
    public required string Status { get; set; }
    public string? ClaimToken { get; set; }
    public DateTime? ClaimedUntilUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}
