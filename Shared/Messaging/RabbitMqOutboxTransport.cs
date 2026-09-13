using RabbitMQ.Client;
using ProjectY.Shared.Observability;
using System.Text;

namespace ProjectY.Shared.Messaging;

public sealed class RabbitMqOutboxTransport : IOutboxTransport
{
    private readonly OutboxRelayOptions _options;
    private readonly IRabbitMqConnectionProvider _connectionProvider;

    public RabbitMqOutboxTransport(
        OutboxRelayOptions options,
        IRabbitMqConnectionProvider connectionProvider)
    {
        _options = options;
        _connectionProvider = connectionProvider;
    }

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = MessagingTraceContext.StartProducerActivity(
            "rabbitmq",
            message.Destination,
            message.TraceParent,
            message.TraceState);

        try
        {
            await using var connection = await _connectionProvider.CreateAsync(cancellationToken);
            // With tracking on, BasicPublishAsync waits for the broker's confirmation and
            // throws when the message is nacked or returned as unroutable. In the 6.x
            // client that was ConfirmSelect plus WaitForConfirmsOrDie, and a return went
            // unnoticed. The options are built per channel because they carry the
            // limiter for outstanding confirmations.
            await using var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            await channel.QueueDeclareAsync(
                message.Destination,
                durable: true,
                exclusive: false,
                autoDelete: false,
                cancellationToken: cancellationToken);

            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = message.Id.ToString("D"),
                Type = message.EventType,
                Headers = new Dictionary<string, object?>()
            };
            MessagingTraceContext.InjectCurrent(
                properties.Headers,
                message.TraceParent,
                message.TraceState);

            // The deadline covers the confirmation, as WaitForConfirmsOrDie's timeout did.
            using var confirmation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            confirmation.CancelAfter(_options.ConfirmationTimeout);
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: message.Destination,
                mandatory: true,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(message.Payload),
                cancellationToken: confirmation.Token);
        }
        catch (Exception exception)
        {
            MessagingTraceContext.RecordException(activity, exception);
            throw;
        }
    }
}
