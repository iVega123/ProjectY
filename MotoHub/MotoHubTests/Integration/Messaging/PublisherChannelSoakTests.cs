using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using ProjectY.Shared.Messaging;
using System.Text.Json;

namespace MotoHubTests.Integration.Messaging;

public sealed class PublisherChannelSoakTests : IAsyncLifetime
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RepeatedOutboxPublications_DoNotAccumulateBrokerChannels()
    {
        var options = new OutboxRelayOptions
        {
            ServiceName = "soak",
            HostName = _broker.Hostname,
            Port = _broker.GetMappedPublicPort(5672),
            VirtualHost = "/",
            UserName = "test",
            Password = _password
        };
        var transport = new RabbitMqOutboxTransport(options, new RabbitMqConnectionProvider(options));
        for (var batch = 0; batch < 4; batch++)
        {
            for (var index = 0; index < 750; index++)
            {
                await transport.PublishAsync(new OutboxMessage
                {
                    AggregateType = "soak",
                    AggregateId = "soak",
                    EventType = "soak.v1",
                    Destination = "soak",
                    Payload = "{}"
                }, CancellationToken.None);
            }
            // Inspect the broker, rather than merely verifying Dispose on a mock.
            var result = await _broker.ExecAsync(["rabbitmqctl", "list_channels", "--quiet", "--formatter=json"]);
            Assert.Equal(0, result.ExitCode);
            using var channels = JsonDocument.Parse(result.Stdout);
            Assert.Equal(0, channels.RootElement.GetArrayLength());
        }
        using var connection = new RabbitMqConnectionProvider(options).Create();
        using var channel = connection.CreateModel();
        Assert.Equal(3000u, channel.MessageCount("soak"));
    }
}
