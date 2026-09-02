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

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = MessagingTraceContext.StartProducerActivity(
            "rabbitmq",
            message.Destination,
            message.TraceParent,
            message.TraceState);

        try
        {
            using var connection = _connectionProvider.Create();
            using var channel = connection.CreateModel();
            channel.QueueDeclare(message.Destination, durable: true, exclusive: false, autoDelete: false);
            channel.ConfirmSelect();

            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.MessageId = message.Id.ToString("D");
            properties.Type = message.EventType;
            properties.Headers = new Dictionary<string, object>();
            MessagingTraceContext.InjectCurrent(
                properties.Headers,
                message.TraceParent,
                message.TraceState);

            channel.BasicPublish(
                exchange: string.Empty,
                routingKey: message.Destination,
                mandatory: true,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(message.Payload));
            channel.WaitForConfirmsOrDie(_options.ConfirmationTimeout);

            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            MessagingTraceContext.RecordException(activity, exception);
            throw;
        }
    }
}
