using Moq;
using ProjectY.Shared.Messaging;
using RabbitMQ.Client;

namespace MotoHubTests.Unit.Messaging;

public sealed class RabbitMqOutboxTransportTests
{
    [Fact]
    public async Task PublishAsync_EnablesAndWaitsForPublisherConfirms()
    {
        var channel = new Mock<IModel>();
        var properties = new Mock<IBasicProperties>();
        channel.Setup(item => item.CreateBasicProperties()).Returns(properties.Object);
        var connection = new Mock<IConnection>();
        connection.Setup(item => item.CreateModel()).Returns(channel.Object);
        var connectionProvider = new Mock<IRabbitMqConnectionProvider>();
        connectionProvider.Setup(item => item.Create()).Returns(connection.Object);
        var options = new OutboxRelayOptions
        {
            ServiceName = "test",
            HostName = "rabbitmq",
            VirtualHost = "test",
            UserName = "test",
            Password = "test"
        };
        var transport = new RabbitMqOutboxTransport(options, connectionProvider.Object);
        var message = new OutboxMessage
        {
            AggregateType = "motorcycle",
            AggregateId = "motorcycle-1",
            AggregateSequence = 0,
            EventType = "motorcycle.updated.v1",
            Destination = "motorcycle-events",
            Payload = "{}"
        };

        await transport.PublishAsync(message, CancellationToken.None);

        channel.Verify(item => item.QueueDeclare(
            message.Destination,
            true,
            false,
            false,
            It.IsAny<IDictionary<string, object>>()), Times.Once);
        channel.Verify(item => item.ConfirmSelect(), Times.Once);
        channel.Verify(item => item.WaitForConfirmsOrDie(options.ConfirmationTimeout), Times.Once);
        channel.Verify(item => item.BasicPublish(
            string.Empty,
            message.Destination,
            true,
            properties.Object,
            It.IsAny<ReadOnlyMemory<byte>>()), Times.Once);
        properties.VerifySet(item => item.MessageId = message.Id.ToString("D"), Times.Once);
    }
}
