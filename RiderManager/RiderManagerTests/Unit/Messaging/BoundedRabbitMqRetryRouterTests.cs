using Moq;
using RabbitMQ.Client;
using RiderManager.Services.RabbitMQService;
using ProjectY.Shared.Observability;
using System.Text;

namespace RiderManagerTests.Unit.Messaging;

public sealed class BoundedRabbitMqRetryRouterTests
{
    [Fact]
    public void RouteFailure_BeforeLimit_RepublishesPersistentlyAtEndOfSourceQueue()
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
            "rider-info",
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
            string.Empty,
            "rider-poison",
            true,
            outgoing.Object,
            body), Times.Once);
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
