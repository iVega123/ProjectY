namespace ProjectY.Shared.Messaging;

public interface IOutboxTransport
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}
