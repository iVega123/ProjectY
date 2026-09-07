using Moq;
using RabbitMQ.Client;
using ProjectY.Shared.Messaging;
using ProjectY.Shared.Observability;
using System.Text;

namespace RiderManagerTests.Unit.Messaging;

public sealed class BoundedRabbitMqRetryRouterTests
{
    [Fact]
    public void RouteFailure_BeforeLimit_PublishesPersistentlyToDelayedQueue()
    {
        var (channel, original, outgoing) = CreateChannel();
        var traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        original.Object.Headers[MessagingTraceContext.TraceParentHeader] = Encoding.UTF8.GetBytes(traceParent);
        var router = new BoundedRabbitMqRetryRouter();
        ReadOnlyMemory<byte> body = "registration"u8.ToArray();

        var route = router.RouteFailure(
            channel.Object,
            "rider-info",
            "rider-poison",
            original.Object,
            body);

        Assert.Equal(FailureRoute.Retry, route);
        Assert.True(outgoing.Object.Persistent);
        Assert.Equal(1, outgoing.Object.Headers[BoundedRabbitMqRetryRouter.RetryHeader]);
        Assert.Equal(
            traceParent,
            Encoding.UTF8.GetString(
                Assert.IsType<byte[]>(outgoing.Object.Headers[MessagingTraceContext.TraceParentHeader])));
        channel.Verify(item => item.BasicPublish(
            string.Empty,
            "rider-info.retry.1",
            true,
            outgoing.Object,
            body), Times.Once);
        channel.Verify(item => item.ConfirmSelect(), Times.Once);
        channel.Verify(item => item.WaitForConfirmsOrDie(
            BoundedRabbitMqRetryRouter.ConfirmationTimeout), Times.Once);
    }

    [Fact]
    public void RouteFailure_AtLimit_QuarantinesMessageInDurablePoisonQueue()
    {
        var (channel, original, outgoing) = CreateChannel();
        original.Object.Headers = new Dictionary<string, object>
        {
            [BoundedRabbitMqRetryRouter.RetryHeader] = BoundedRabbitMqRetryRouter.MaximumRetryCount
        };
        var router = new BoundedRabbitMqRetryRouter();
        ReadOnlyMemory<byte> body = "registration"u8.ToArray();

        var route = router.RouteFailure(
            channel.Object,
            "rider-info",
            "rider-poison",
            original.Object,
            body);

        Assert.Equal(FailureRoute.Poison, route);
        Assert.True(outgoing.Object.Persistent);
        channel.Verify(item => item.QueueDeclare(
            "rider-poison",
            true,
            false,
            false,
            It.IsAny<IDictionary<string, object>>()), Times.Once);
        channel.Verify(item => item.BasicPublish(
            "rider-info.dead",
            "rider-info",
            true,
            outgoing.Object,
            body), Times.Once);
    }

    [Fact]
    public void RouteFailure_WhenConfirmationFails_DoesNotReportSuccessfulRouting()
    {
        var (channel, original, _) = CreateChannel();
        channel.Setup(item => item.WaitForConfirmsOrDie(It.IsAny<TimeSpan>()))
            .Throws(new IOException("Broker confirmation failed"));
        Assert.Throws<IOException>(() => new BoundedRabbitMqRetryRouter().RouteFailure(
            channel.Object, "source", "poison", original.Object, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void RouteFailure_PermanentFailure_DoesNotEnterRetryQueue()
    {
        var (channel, original, outgoing) = CreateChannel();
        Assert.Equal(FailureRoute.Poison, new BoundedRabbitMqRetryRouter().RouteFailure(
            channel.Object, "source", "poison", original.Object, ReadOnlyMemory<byte>.Empty, permanent: true));
        channel.Verify(item => item.BasicPublish("source.dead", "source", true,
            outgoing.Object, ReadOnlyMemory<byte>.Empty), Times.Once);
    }
    private static (
        Mock<IModel> Channel,
        Mock<IBasicProperties> Original,
        Mock<IBasicProperties> Outgoing) CreateChannel()
    {
        var channel = new Mock<IModel>();
        var original = new Mock<IBasicProperties>();
        original.SetupAllProperties();
        original.Object.Headers = new Dictionary<string, object>();
        var outgoing = new Mock<IBasicProperties>();
        outgoing.SetupAllProperties();
        channel.Setup(item => item.CreateBasicProperties()).Returns(outgoing.Object);
        return (channel, original, outgoing);
    }
}
