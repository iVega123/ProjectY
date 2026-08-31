namespace ProjectY.Shared.Messaging;

public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string AggregateType { get; set; }
    public required string AggregateId { get; set; }
    public long AggregateSequence { get; set; }
    public required string EventType { get; set; }
    public required string Destination { get; set; }
    public required string Payload { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAtUtc { get; set; }
    public int PublishAttempts { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
}
