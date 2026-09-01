using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MotoHub.Configurations;
using MotoHub.CrossCutting;
using MotoHub.Data;
using MotoHub.Models;
using MotoHub.Repositories;
using MotoHub.Services;
using MotoHub.Services.RabbitMQ;
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
    [Trait("Guarantee", "ADR-0009#transactional-outbox")]
    public async Task CommittedMessages_SurviveRelayRestartAndDrainInAggregateOrderAfterBrokerRecovery()
    {
        var transport = new RecoverableOutboxTransport { IsAvailable = false };

        await using (var firstProcess = CreateRelayProvider(transport))
        {
            await using (var scope = firstProcess.CreateAsyncScope())
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

            var relayBeforeRestart = firstProcess.GetRequiredService<OutboxRelay<ApplicationDbContext>>();
            Assert.Equal(0, await relayBeforeRestart.DispatchOnceAsync());

            await using var verificationScope = firstProcess.CreateAsyncScope();
            var verificationContext = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await verificationContext.Motorcycles.CountAsync());
            Assert.Equal(2, await verificationContext.OutboxMessages.CountAsync(message => message.PublishedAtUtc == null));
            var failed = await verificationContext.OutboxMessages.SingleAsync(message => message.AggregateSequence == 0);
            Assert.Equal(1, failed.PublishAttempts);
            failed.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await verificationContext.SaveChangesAsync();
        }

        transport.IsAvailable = true;
        await using var restartedProcess = CreateRelayProvider(transport);
        var relayAfterRestart = restartedProcess.GetRequiredService<OutboxRelay<ApplicationDbContext>>();
        Assert.Equal(2, await relayAfterRestart.DispatchOnceAsync());

        Assert.Equal(
            ["motorcycle.first.v1", "motorcycle.second.v1"],
            transport.Published.Select(message => message.EventType));
        await using (var scope = restartedProcess.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(2, await context.OutboxMessages.CountAsync(message => message.PublishedAtUtc != null));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#transactional-outbox")]
    public async Task DomainMutationAndOutboxInsert_RollBackTogetherWhenSaveFails()
    {
        await using (var seed = CreateContext())
        {
            await seed.Database.MigrateAsync();
            seed.Motorcycles.Add(new Motorcycle
            {
                Id = "motorcycle-atomic",
                LicensePlate = "ATM-0001",
                Model = "Atomic outbox proof",
                Year = 2026,
                RegistrationDate = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var publisher = new MessagingPublisherService(
                context,
                new RabbitMQOptions
                {
                    HostName = "unused",
                    VirtualHost = "unused",
                    UserName = "unused",
                    Password = "unused",
                    LicenceUpdateQueueName = new string('q', 201)
                });
            var service = new MotorcycleService(
                new MotorcycleRepository(context),
                Mock.Of<IMapper>(),
                publisher,
                Mock.Of<IRentalOperationService>());

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                service.UpdateMotorcycleAsync("ATM-0001", "ATM-0002"));
        }

        await using var verification = CreateContext();
        Assert.Equal(
            "ATM-0001",
            (await verification.Motorcycles.SingleAsync()).LicensePlate);
        Assert.Empty(await verification.OutboxMessages.ToListAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#leased-outbox-relay")]
    public async Task ConcurrentRelays_ClaimOnlyOneHeadMessagePerAggregate()
    {
        var transport = new BlockingOutboxTransport();
        await using var provider = CreateRelayProvider(transport, batchSize: 1);

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

    private ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_database.GetConnectionString())
            .Options);

    private ServiceProvider CreateRelayProvider(IOutboxTransport transport, int batchSize = 100)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(_database.GetConnectionString()));
        services.AddSingleton(transport);
        services.AddSingleton(new OutboxRelayOptions
        {
            ServiceName = "moto-hub-test",
            HostName = "unused",
            VirtualHost = "unused",
            UserName = "unused",
            Password = "unused",
            BatchSize = batchSize,
            PollInterval = TimeSpan.FromMilliseconds(10),
            ClaimLeaseDuration = TimeSpan.FromMinutes(1)
        });
        services.AddTransient<OutboxRelay<ApplicationDbContext>>();
        return services.BuildServiceProvider();
    }
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
