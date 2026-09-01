using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MotoHub.Data;
using ProjectY.Shared.Messaging;

namespace MotoHubTests.Unit.Messaging;

public sealed class OutboxRelayBackgroundTests
{
    [Fact]
    [Trait("Guarantee", "ADR-0009#transactional-outbox")]
    public async Task InfrastructureFailure_DoesNotStopRelayAndIsRetried()
    {
        var scopeFactory = new FailingScopeFactory();
        var relay = new OutboxRelay<ApplicationDbContext>(
            scopeFactory,
            Mock.Of<IOutboxTransport>(),
            new OutboxRelayOptions
            {
                ServiceName = "moto-hub-test",
                HostName = "unused",
                VirtualHost = "unused",
                UserName = "unused",
                Password = "unused",
                PollInterval = TimeSpan.FromMilliseconds(10)
            },
            Mock.Of<ILogger<OutboxRelay<ApplicationDbContext>>>());

        await relay.StartAsync(CancellationToken.None);
        await scopeFactory.RetryObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await relay.StopAsync(CancellationToken.None);

        Assert.True(scopeFactory.Attempts >= 2);
    }

    private sealed class FailingScopeFactory : IServiceScopeFactory
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);
        public TaskCompletionSource RetryObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _attempts) >= 2)
            {
                RetryObserved.TrySetResult();
            }

            throw new InvalidOperationException("Database infrastructure unavailable.");
        }
    }
}
