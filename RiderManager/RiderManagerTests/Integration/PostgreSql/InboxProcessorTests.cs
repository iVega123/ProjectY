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
