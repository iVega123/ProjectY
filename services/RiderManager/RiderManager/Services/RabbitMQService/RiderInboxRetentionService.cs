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
        var expiredRiders = await context.InboxImageParts
            .Where(part => part.ReceivedAtUtc < imageCutoff)
            .Select(part => part.UserId).Distinct().ToListAsync(cancellationToken);
        var deletedParts = 0;
        foreach (var userId in expiredRiders)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            // Match the handler lock, then re-evaluate expiry after any in-flight assembly commits.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({userId}, 0))", cancellationToken);
            deletedParts += await context.InboxImageParts
                .Where(part => part.UserId == userId && context.InboxImageParts.Any(expired =>
                    expired.UserId == part.UserId && expired.FileName == part.FileName
                    && expired.ReceivedAtUtc < imageCutoff))
                .ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return deletedMessages + deletedParts;
    }
}
