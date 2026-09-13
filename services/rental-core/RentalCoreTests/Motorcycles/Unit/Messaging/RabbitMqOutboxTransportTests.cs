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
        var broker = new FakeBroker();
        var transport = new RabbitMqOutboxTransport(Options(), broker.Provider);
        var message = Message();

        await transport.PublishAsync(message, CancellationToken.None);

        broker.Connection.Verify(item => item.CreateChannelAsync(
            It.Is<CreateChannelOptions>(options =>
                options.PublisherConfirmationsEnabled && options.PublisherConfirmationTrackingEnabled),
            It.IsAny<CancellationToken>()), Times.Once);
        broker.Channel.Verify(item => item.QueueDeclareAsync(
            message.Destination,
            true,
            false,
            false,
            It.IsAny<IDictionary<string, object?>>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
        broker.Channel.Verify(item => item.BasicPublishAsync(
            string.Empty,
            message.Destination,
            true,
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(broker.Published);
        Assert.True(broker.Published.Persistent);
        Assert.Equal(message.Id.ToString("D"), broker.Published.MessageId);
        // The wait for the confirmation has a deadline.
        Assert.True(broker.PublishToken.CanBeCanceled);
    }

    [Fact]
    public async Task PublishAsync_ContinuesStoredRequestTraceAcrossOutboxAndConsumer()
    {
        using var listener = ListenToProjectYMessaging();
        using var request = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        Assert.NotNull(request);
        request.TraceStateString = "vendor=value";
        var message = Message();
        var requestTraceId = request.TraceId;
        var requestSpanId = request.SpanId;
        Assert.Equal(request.Id, message.TraceParent);
        Assert.Equal(request.TraceStateString, message.TraceState);
        request.Stop();

        var broker = new FakeBroker();
        var transport = new RabbitMqOutboxTransport(Options(), broker.Provider);

        await transport.PublishAsync(message, CancellationToken.None);

        var headers = broker.Published?.Headers;
        Assert.NotNull(headers);
        var publishedTraceParent = Encoding.UTF8.GetString(
            Assert.IsType<byte[]>(headers[MessagingTraceContext.TraceParentHeader]));
        Assert.True(ActivityContext.TryParse(publishedTraceParent, null, true, out var publishedContext));
        Assert.Equal(requestTraceId, publishedContext.TraceId);
        Assert.NotEqual(requestSpanId, publishedContext.SpanId);
        Assert.Equal(
            "vendor=value",
            Encoding.UTF8.GetString(
                Assert.IsType<byte[]>(headers[MessagingTraceContext.TraceStateHeader])));

        using var consumer = MessagingTraceContext.StartConsumerActivity(
            "rabbitmq",
            message.Destination,
            headers,
            message.Id.ToString("D"));
        Assert.NotNull(consumer);
        Assert.Equal(requestTraceId, consumer.TraceId);
        Assert.Equal(publishedContext.SpanId, consumer.ParentSpanId);
        Assert.Equal("vendor=value", consumer.TraceStateString);
        Assert.Equal(ActivityKind.Consumer, consumer.Kind);
    }

    private static OutboxRelayOptions Options() => new()
    {
        ServiceName = "test",
        HostName = "rabbitmq",
        VirtualHost = "test",
        UserName = "test",
        Password = "test"
    };

    private static OutboxMessage Message() => new()
    {
        AggregateType = "motorcycle",
        AggregateId = "motorcycle-1",
        AggregateSequence = 0,
        EventType = "motorcycle.updated.v1",
        Destination = "motorcycle-events",
        Payload = "{}"
    };

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

    private sealed class FakeBroker
    {
        public Mock<IChannel> Channel { get; } = new();
        public Mock<IConnection> Connection { get; } = new();
        public IRabbitMqConnectionProvider Provider { get; }
        public BasicProperties? Published { get; private set; }
        public CancellationToken PublishToken { get; private set; }

        public FakeBroker()
        {
            Channel
                .Setup(item => item.BasicPublishAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<BasicProperties>(),
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<CancellationToken>()))
                .Callback((string _, string _, bool _, BasicProperties properties, ReadOnlyMemory<byte> _, CancellationToken token) =>
                {
                    Published = properties;
                    PublishToken = token;
                })
                .Returns(ValueTask.CompletedTask);
            Connection
                .Setup(item => item.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Channel.Object);
            var provider = new Mock<IRabbitMqConnectionProvider>();
            provider
                .Setup(item => item.CreateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Connection.Object);
            Provider = provider.Object;
        }
    }
}
