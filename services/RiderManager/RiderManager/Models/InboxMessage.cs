namespace RiderManager.Models;

public sealed class InboxMessage
{
    public required string MessageId { get; set; }
    public required string ConsumerName { get; set; }
    public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
}
