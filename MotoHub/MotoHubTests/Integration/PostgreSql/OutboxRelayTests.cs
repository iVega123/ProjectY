using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MotoHub.Data;
using MotoHub.Models;
using ProjectY.Shared.Messaging;
using Testcontainers.PostgreSql;

namespace MotoHubTests.Integration.PostgreSql;

public sealed class OutboxRelayTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("moto_hub_outbox")
        .WithUsername("projecty")
        .Build();

    public Task InitializeAsync() => _database.StartAsync();

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CommittedMessages_SurviveRelayRestartAndDrainInAggregateOrderAfterBrokerRecovery()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(_database.GetConnectionString()));
        var transport = new RecoverableOutboxTransport { IsAvailable = false };
        services.AddSingleton<IOutboxTransport>(transport);
        services.AddSingleton(new OutboxRelayOptions
        {
            ServiceName = "moto-hub-test",
            HostName = "unused",
            VirtualHost = "unused",
            UserName = "unused",
            Password = "unused",
            PollInterval = TimeSpan.FromMilliseconds(10)
        });
        services.AddSingleton<OutboxRelay<ApplicationDbContext>>();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.Database.MigrateAsync();
            context.Motorcycles.Add(new Motorcycle
            {
                Id = "motorcycle-1",
                LicensePlate = "OUT-0001",
                Model = "Outbox proof",
                Year = 2026,
                RegistrationDate = DateTime.UtcNow
            });
            context.OutboxMessages.AddRange(
                Message(sequence: 1, eventType: "motorcycle.second.v1"),
                Message(sequence: 0, eventType: "motorcycle.first.v1"));
            await context.SaveChangesAsync();
        }

        var relayAfterCommit = provider.GetRequiredService<OutboxRelay<ApplicationDbContext>>();
        Assert.Equal(0, await relayAfterCommit.DispatchOnceAsync());

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await context.Motorcycles.CountAsync());
            Assert.Equal(2, await context.OutboxMessages.CountAsync(message => message.PublishedAtUtc == null));
            var failed = await context.OutboxMessages.SingleAsync(message => message.AggregateSequence == 0);
            Assert.Equal(1, failed.PublishAttempts);
            failed.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await context.SaveChangesAsync();
        }

        transport.IsAvailable = true;
        Assert.Equal(2, await relayAfterCommit.DispatchOnceAsync());

        Assert.Equal(
            ["motorcycle.first.v1", "motorcycle.second.v1"],
            transport.Published.Select(message => message.EventType));
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(2, await context.OutboxMessages.CountAsync(message => message.PublishedAtUtc != null));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentRelays_ClaimOnlyOneHeadMessagePerAggregate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(_database.GetConnectionString()));
        var transport = new BlockingOutboxTransport();
        services.AddSingleton<IOutboxTransport>(transport);
        services.AddSingleton(new OutboxRelayOptions
        {
            ServiceName = "moto-hub-test",
            HostName = "unused",
            VirtualHost = "unused",
            UserName = "unused",
            Password = "unused",
            BatchSize = 1,
            ClaimLeaseDuration = TimeSpan.FromMinutes(1)
        });
        services.AddTransient<OutboxRelay<ApplicationDbContext>>();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.Database.MigrateAsync();
            context.OutboxMessages.AddRange(
                Message(sequence: 0, eventType: "motorcycle.first.v1"),
                Message(sequence: 1, eventType: "motorcycle.second.v1"));
            await context.SaveChangesAsync();
        }

        var firstRelay = provider.GetRequiredService<OutboxRelay<ApplicationDbContext>>();
        var secondRelay = provider.GetRequiredService<OutboxRelay<ApplicationDbContext>>();
        var firstDispatch = firstRelay.DispatchOnceAsync();
        await transport.FirstPublishStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, await secondRelay.DispatchOnceAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, transport.PublishCalls);

        transport.ReleaseFirstPublish();
        Assert.Equal(1, await firstDispatch);
        Assert.Equal(1, await secondRelay.DispatchOnceAsync());
        Assert.Equal(
            ["motorcycle.first.v1", "motorcycle.second.v1"],
            transport.PublishedEventTypes);
    }

    private static OutboxMessage Message(long sequence, string eventType) => new()
    {
        AggregateType = "motorcycle",
        AggregateId = "motorcycle-1",
        AggregateSequence = sequence,
        EventType = eventType,
        Destination = "motorcycle-events",
        Payload = "{}"
    };
}

internal sealed class RecoverableOutboxTransport : IOutboxTransport
{
    public bool IsAvailable { get; set; }
    public List<OutboxMessage> Published { get; } = [];

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Broker unavailable.");
        }

        Published.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class BlockingOutboxTransport : IOutboxTransport
{
    private readonly TaskCompletionSource<bool> _releaseFirstPublish = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _publishedEventTypes = new();
    private int _publishCalls;

    public TaskCompletionSource<bool> FirstPublishStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public int PublishCalls => Volatile.Read(ref _publishCalls);
    public string[] PublishedEventTypes => _publishedEventTypes.ToArray();

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _publishCalls);
        _publishedEventTypes.Enqueue(message.EventType);
        if (call == 1)
        {
            FirstPublishStarted.TrySetResult(true);
            await _releaseFirstPublish.Task.WaitAsync(cancellationToken);
        }
    }

    public void ReleaseFirstPublish() => _releaseFirstPublish.TrySetResult(true);
}
