using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using ProjectY.Shared.Messaging;
using RabbitMQ.Client;
using System.Text;

namespace RiderManagerTests.Integration.Messaging;

public sealed class DurableRetryTests : IAsyncLifetime
{
    private readonly string _password = Guid.NewGuid().ToString("N");
    private IContainer _broker = null!;

    public async Task InitializeAsync()
    {
        _broker = new ContainerBuilder("rabbitmq:4-management")
            .WithEnvironment("RABBITMQ_DEFAULT_USER", "test")
            .WithEnvironment("RABBITMQ_DEFAULT_PASS", _password)
            .WithPortBinding(5672, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5672))
            .Build();
        await _broker.StartAsync();
    }

    public Task DisposeAsync() => _broker.DisposeAsync().AsTask();

    private IConnection Connect() => new ConnectionFactory
    {
        HostName = _broker.Hostname,
        Port = _broker.GetMappedPublicPort(5672),
        UserName = "test",
        Password = _password,
        AutomaticRecoveryEnabled = false
    }.CreateConnection();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DelayedRetry_SurvivesBrokerRestart_QuarantinesPoison_AndDoesNotBlockGoodMessage()
    {
        const string source = "rider_info_queue";
        const string poison = "rider_info_poison_queue";
        var router = new BoundedRabbitMqRetryRouter();
        using (var connection = Connect())
        using (var channel = connection.CreateModel())
        {
            router.DeclareTopology(channel, source, poison);
            channel.ConfirmSelect();
            foreach (var id in new[] { "malformed", "good" })
            {
                var properties = channel.CreateBasicProperties();
                properties.Persistent = true;
                properties.MessageId = id;
                channel.BasicPublish("", source, true, properties, Encoding.UTF8.GetBytes(id));
            }
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
            var failed = await GetAsync(channel, source);
            Assert.Equal("malformed", failed.BasicProperties.MessageId);
            Assert.Equal(FailureRoute.Retry, router.RouteFailure(channel, source, poison, failed.BasicProperties, failed.Body));
            channel.BasicAck(failed.DeliveryTag, false);
            var good = await GetAsync(channel, source);
            Assert.Equal("good", good.BasicProperties.MessageId);
            channel.BasicAck(good.DeliveryTag, false);
            Assert.Null(channel.BasicGet(source, false));
        }

        await _broker.StopAsync();
        await _broker.StartAsync();

        using var recoveredConnection = Connect();
        using var recoveredChannel = recoveredConnection.CreateModel();
        for (var attempt = 1; attempt <= BoundedRabbitMqRetryRouter.MaximumRetryCount; attempt++)
        {
            var retry = await GetAsync(recoveredChannel, source);
            Assert.True(retry.BasicProperties.Persistent);
            Assert.Equal("malformed", retry.BasicProperties.MessageId);
            Assert.Equal(attempt, Convert.ToInt32(retry.BasicProperties.Headers[BoundedRabbitMqRetryRouter.RetryHeader]));
            var route = router.RouteFailure(recoveredChannel, source, poison, retry.BasicProperties, retry.Body);
            Assert.Equal(attempt == 3 ? FailureRoute.Poison : FailureRoute.Retry, route);
            recoveredChannel.BasicAck(retry.DeliveryTag, false);
        }
        var dead = await GetAsync(recoveredChannel, poison);
        Assert.Equal("malformed", dead.BasicProperties.MessageId);
        Assert.True(dead.BasicProperties.Persistent);
        recoveredChannel.BasicAck(dead.DeliveryTag, false);
        Assert.Null(recoveredChannel.BasicGet(source, false));
    }

    private static async Task<BasicGetResult> GetAsync(IModel channel, string queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            var message = channel.BasicGet(queue, false);
            if (message is not null) { return message; }
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException($"No delivery from {queue}");
    }
}
