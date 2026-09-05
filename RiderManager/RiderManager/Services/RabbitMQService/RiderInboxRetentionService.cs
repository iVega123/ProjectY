using Microsoft.EntityFrameworkCore;
using RiderManager.Data;

namespace RiderManager.Services.RabbitMQService;

public sealed class RiderInboxRetentionOptions
{
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan ImageRetentionPeriod { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed class RiderInboxRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RiderInboxRetentionOptions _options;
    private readonly TimeProvider _timeProvider;

    public RiderInboxRetentionService(
        IServiceScopeFactory scopeFactory,
        RiderInboxRetentionOptions options,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SweepInterval, _timeProvider);
        do
        {
            await DeleteExpiredAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _options.RetentionPeriod;
        var deletedMessages = await context.InboxMessages
            .Where(message => message.ProcessedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        var imageCutoff = _timeProvider.GetUtcNow().UtcDateTime - _options.ImageRetentionPeriod;
        var deletedParts = await context.InboxImageParts
            .Where(part => context.InboxImageParts.Any(expired => expired.UserId == part.UserId
                && expired.FileName == part.FileName && expired.ReceivedAtUtc < imageCutoff))
            .ExecuteDeleteAsync(cancellationToken);
        return deletedMessages + deletedParts;
    }
}
