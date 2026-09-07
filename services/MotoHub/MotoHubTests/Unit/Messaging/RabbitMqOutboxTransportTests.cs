using System.Diagnostics;
using System.Text;
using Moq;
using ProjectY.Shared.Messaging;
using ProjectY.Shared.Observability;
using RabbitMQ.Client;

namespace MotoHubTests.Unit.Messaging;

public sealed class RabbitMqOutboxTransportTests
{
    [Fact]
    public async Task PublishAsync_EnablesAndWaitsForPublisherConfirms()
    {
        var channel = new Mock<IModel>();
        var properties = new Mock<IBasicProperties>();
        properties.SetupAllProperties();
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

    [Fact]
    public async Task PublishAsync_ContinuesStoredRequestTraceAcrossOutboxAndConsumer()
    {
        using var listener = ListenToProjectYMessaging();
        using var request = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        Assert.NotNull(request);
        request.TraceStateString = "vendor=value";
        var message = new OutboxMessage
        {
            AggregateType = "motorcycle",
            AggregateId = "motorcycle-1",
            AggregateSequence = 0,
            EventType = "motorcycle.updated.v1",
            Destination = "motorcycle-events",
            Payload = "{}"
        };
        var requestTraceId = request.TraceId;
        var requestSpanId = request.SpanId;
        Assert.Equal(request.Id, message.TraceParent);
        Assert.Equal(request.TraceStateString, message.TraceState);
        request.Stop();

        var channel = new Mock<IModel>();
        var properties = new Mock<IBasicProperties>();
        properties.SetupAllProperties();
        channel.Setup(item => item.CreateBasicProperties()).Returns(properties.Object);
        var connection = new Mock<IConnection>();
        connection.Setup(item => item.CreateModel()).Returns(channel.Object);
        var connectionProvider = new Mock<IRabbitMqConnectionProvider>();
        connectionProvider.Setup(item => item.Create()).Returns(connection.Object);
        var transport = new RabbitMqOutboxTransport(
            new OutboxRelayOptions
            {
                ServiceName = "test",
                HostName = "rabbitmq",
                VirtualHost = "test",
                UserName = "test",
                Password = "test"
            },
            connectionProvider.Object);

        await transport.PublishAsync(message, CancellationToken.None);

        Assert.NotNull(properties.Object.Headers);
        var publishedTraceParent = Encoding.UTF8.GetString(
            Assert.IsType<byte[]>(properties.Object.Headers[MessagingTraceContext.TraceParentHeader]));
        Assert.True(ActivityContext.TryParse(publishedTraceParent, null, true, out var publishedContext));
        Assert.Equal(requestTraceId, publishedContext.TraceId);
        Assert.NotEqual(requestSpanId, publishedContext.SpanId);
        Assert.Equal(
            "vendor=value",
            Encoding.UTF8.GetString(
                Assert.IsType<byte[]>(properties.Object.Headers[MessagingTraceContext.TraceStateHeader])));

        using var consumer = MessagingTraceContext.StartConsumerActivity(
            "rabbitmq",
            message.Destination,
            properties.Object.Headers,
            message.Id.ToString("D"));
        Assert.NotNull(consumer);
        Assert.Equal(requestTraceId, consumer.TraceId);
        Assert.Equal(publishedContext.SpanId, consumer.ParentSpanId);
        Assert.Equal("vendor=value", consumer.TraceStateString);
        Assert.Equal(ActivityKind.Consumer, consumer.Kind);
    }

    private static ActivityListener ListenToProjectYMessaging()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MessagingTraceContext.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
