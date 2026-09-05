using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProjectY.Shared.Messaging;
using RiderManager.Data;
using RiderManager.Entities;
using RiderManager.Managers;
using RiderManager.Models;
using RiderManager.Services.RabbitMQService;
using Testcontainers.PostgreSql;

namespace RiderManagerTests.Integration.PostgreSql;

public sealed class InboxProcessorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17.11-alpine3.24")
        .WithDatabase("rider_manager_inbox")
        .WithUsername("projecty")
        .Build();

    private DbContextOptions<ApplicationDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_database.GetConnectionString())
            .Options;
        await using var context = new ApplicationDbContext(_options);
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#transactional-inbox")]
    public async Task SameMessageProcessedConcurrently_ProducesOneDatabaseEffect()
    {
        var messageId = Guid.NewGuid().ToString("D");
        var first = ProcessRegistrationAsync(messageId, "first@example.test");
        var second = ProcessRegistrationAsync(messageId, "second@example.test");

        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        await using var verification = new ApplicationDbContext(_options);
        Assert.Equal(1, await verification.Riders.CountAsync());
        Assert.Equal(1, await verification.InboxMessages.CountAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#transactional-inbox")]
    public async Task ImageRedelivery_UsesInboxAndCallsIdempotentUploadOnce()
    {
        var manager = new Mock<IRiderManager>();
        await using var context = new ApplicationDbContext(_options);
        var handler = new RiderInboxMessageHandler(
            context,
            new RiderInboxProcessor(context),
            manager.Object);
        var message = new AuthenticatedQueueMessage<ImagePart>(
            Guid.NewGuid().ToString("D"),
            new ImagePart
            {
                UserId = "rider-image",
                FileName = "rider-image_20260831220000.png",
                SequenceNumber = 0,
                Content = [1, 2, 3],
                EndOfFile = true
            });

        Assert.True(await handler.HandleImagePartAsync(message));
        Assert.False(await handler.HandleImagePartAsync(message));

        manager.Verify(item => item.UpdateRiderImageAsync(
            "rider-image",
            It.IsAny<IFormFile>(),
            "rider-image_20260831220000.png"), Times.Once);
        Assert.Empty(await context.InboxImageParts.ToListAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#retention-boundaries")]
    public async Task RetentionSweep_DeletesOnlyExpiredInboxRows()
    {
        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.InboxMessages.AddRange(
                new InboxMessage
                {
                    MessageId = "expired",
                    ConsumerName = "test",
                    ProcessedAtUtc = DateTime.UtcNow.AddDays(-8)
                },
                new InboxMessage
                {
                    MessageId = "current",
                    ConsumerName = "test",
                    ProcessedAtUtc = DateTime.UtcNow
                });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection()
            .AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(_database.GetConnectionString()))
            .BuildServiceProvider();
        var retention = new RiderInboxRetentionService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new RiderInboxRetentionOptions { RetentionPeriod = TimeSpan.FromDays(7) },
            TimeProvider.System);

        Assert.Equal(1, await retention.DeleteExpiredAsync());
        await using var verification = new ApplicationDbContext(_options);
        Assert.Equal("current", (await verification.InboxMessages.SingleAsync()).MessageId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IndependentReplicas_AssembleOutOfOrderPartsExactlyOnce()
    {
        var manager = new Mock<IRiderManager>();
        byte[]? uploaded = null;
        manager.Setup(item => item.UpdateRiderImageAsync("replica-rider", It.IsAny<IFormFile>(), "replica.png"))
            .Callback<string, IFormFile, string>((_, file, _) =>
            {
                using var memory = new MemoryStream();
                file.CopyTo(memory);
                uploaded = memory.ToArray();
            });
        async Task Append(int sequence, bool eof, byte value)
        {
            await using var context = new ApplicationDbContext(_options);
            var handler = new RiderInboxMessageHandler(context, new RiderInboxProcessor(context), manager.Object);
            await handler.HandleImagePartAsync(new AuthenticatedQueueMessage<ImagePart>(
                Guid.NewGuid().ToString("N"), new ImagePart
                {
                    UserId = "replica-rider",
                    FileName = "replica.png",
                    SequenceNumber = sequence,
                    EndOfFile = eof,
                    Content = [value]
                }));
        }

        await Append(2, true, 3);
        await Task.WhenAll(Append(0, false, 1), Append(1, false, 2));
        await Append(0, false, 1); // A late duplicate cannot recreate a completed upload.
        Assert.Equal(new byte[] { 1, 2, 3 }, uploaded);
        manager.Verify(item => item.UpdateRiderImageAsync("replica-rider", It.IsAny<IFormFile>(), "replica.png"), Times.Once);
        await using var verification = new ApplicationDbContext(_options);
        Assert.Empty(await verification.InboxImageParts.ToListAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentReplicas_CannotBothCrossTheUploadSizeLimit()
    {
        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.InboxImageParts.Add(new InboxImagePart
            {
                UserId = "bounded",
                FileName = "bounded.png",
                SequenceNumber = 0,
                Content = new byte[RiderInboxMessageHandler.MaximumUploadBytes - 1]
            });
            await seed.SaveChangesAsync();
        }
        async Task<bool> Append(int sequence)
        {
            await using var context = new ApplicationDbContext(_options);
            var handler = new RiderInboxMessageHandler(context, new RiderInboxProcessor(context), Mock.Of<IRiderManager>());
            try
            {
                return await handler.HandleImagePartAsync(new AuthenticatedQueueMessage<ImagePart>(
                    Guid.NewGuid().ToString("N"), new ImagePart
                    {
                        UserId = "bounded",
                        FileName = "bounded.png",
                        SequenceNumber = sequence,
                        Content = [1]
                    }));
            }
            catch (InvalidDataException) { return false; }
        }
        var results = await Task.WhenAll(Append(1), Append(2));
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        await using var verification = new ApplicationDbContext(_options);
        Assert.Equal(RiderInboxMessageHandler.MaximumUploadBytes,
            await verification.InboxImageParts.SumAsync(part => (long)part.Content.Length));
        Assert.Single(await verification.InboxMessages.ToListAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IncompleteUpload_ExpiresAsAWhole_WhileRecentUploadRemains()
    {
        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.InboxImageParts.AddRange(
                new InboxImagePart
                {
                    UserId = "old",
                    FileName = "old.png",
                    SequenceNumber = 0,
                    Content = [1],
                    ReceivedAtUtc = DateTime.UtcNow.AddHours(-2)
                },
                new InboxImagePart
                {
                    UserId = "old",
                    FileName = "old.png",
                    SequenceNumber = 1,
                    Content = [2],
                    ReceivedAtUtc = DateTime.UtcNow
                },
                new InboxImagePart
                {
                    UserId = "recent",
                    FileName = "recent.png",
                    SequenceNumber = 0,
                    Content = [3],
                    ReceivedAtUtc = DateTime.UtcNow
                });
            await seed.SaveChangesAsync();
        }
        using var services = new ServiceCollection()
            .AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(_database.GetConnectionString()))
            .BuildServiceProvider();
        var retention = new RiderInboxRetentionService(services.GetRequiredService<IServiceScopeFactory>(),
            new RiderInboxRetentionOptions(), TimeProvider.System);
        Assert.Equal(2, await retention.DeleteExpiredAsync());
        await using var verification = new ApplicationDbContext(_options);
        Assert.Equal("recent", (await verification.InboxImageParts.SingleAsync()).UserId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RetentionSweep_WaitsForHandlerLock_AndRechecksExpiry()
    {
        const string rider = "retention-race";
        await using var handlerContext = new ApplicationDbContext(_options);
        handlerContext.InboxImageParts.Add(new InboxImagePart
        {
            UserId = rider, FileName = "race.png", SequenceNumber = 0,
            Content = [1, 2], ReceivedAtUtc = DateTime.UtcNow.AddHours(-2)
        });
        await handlerContext.SaveChangesAsync();
        await using var transaction = await handlerContext.Database.BeginTransactionAsync();
        await handlerContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({rider}, 0))");
        using var services = new ServiceCollection()
            .AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(_database.GetConnectionString()))
            .BuildServiceProvider();
        var retention = new RiderInboxRetentionService(services.GetRequiredService<IServiceScopeFactory>(),
            new RiderInboxRetentionOptions(), TimeProvider.System);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sweep = retention.DeleteExpiredAsync(timeout.Token);
        try
        {
            var waiting = false;
            while (!waiting && !sweep.IsCompleted)
            {
                waiting = await handlerContext.Database.SqlQueryRaw<bool>(
                    """SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted) AS "Value" """)
                    .SingleAsync(timeout.Token);
                if (!waiting) await Task.Delay(10, timeout.Token);
            }
            Assert.True(waiting, "Retention must acquire the handler lock before deleting.");
            await handlerContext.InboxImageParts.Where(p => p.UserId == rider)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ReceivedAtUtc, DateTime.UtcNow));
        }
        finally
        {
            await transaction.CommitAsync();
        }
        Assert.Equal(0, await sweep);
        await using var verification = new ApplicationDbContext(_options);
        Assert.Equal(new byte[] { 1, 2 }, (await verification.InboxImageParts.SingleAsync()).Content);
    }

    private async Task<bool> ProcessRegistrationAsync(string messageId, string email)
    {
        await using var context = new ApplicationDbContext(_options);
        var processor = new RiderInboxProcessor(context);
        return await processor.ProcessAsync(
            messageId,
            RiderInboxMessageHandler.RegistrationConsumer,
            async cancellationToken =>
            {
                context.Riders.Add(new Rider
                {
                    Id = Guid.NewGuid().ToString("D"),
                    UserId = Guid.NewGuid().ToString("D"),
                    Email = email,
                    Name = "Inbox proof",
                    CNPJ = Guid.NewGuid().ToString("N"),
                    DateOfBirth = DateTime.UtcNow.AddYears(-25),
                    CNHNumber = Guid.NewGuid().ToString("N"),
                    CNHType = "A"
                });
                await context.SaveChangesAsync(cancellationToken);
            });
    }
}
