using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProjectY.Shared.Messaging;

public sealed class OutboxRelay<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOutboxTransport _transport;
    private readonly OutboxRelayOptions _options;
    private readonly ILogger<OutboxRelay<TContext>> _logger;

    public OutboxRelay(
        IServiceScopeFactory scopeFactory,
        IOutboxTransport transport,
        OutboxRelayOptions options,
        ILogger<OutboxRelay<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _transport = transport;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Outbox relay infrastructure failure in {ServiceName}; retrying in {RetryDelay}.",
                    _options.ServiceName,
                    _options.PollInterval);
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var publishedCount = 0;

        for (var index = 0; index < _options.BatchSize; index++)
        {
            var claimToken = Guid.NewGuid();
            var message = await ClaimNextAsync(context, claimToken, cancellationToken);
            if (message is null)
            {
                break;
            }

            try
            {
                await _transport.PublishAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await MarkFailedAsync(context, message, claimToken, exception, cancellationToken);
                continue;
            }

            if (await MarkPublishedAsync(context, message, claimToken, cancellationToken))
            {
                publishedCount++;
            }
            else
            {
                _logger.LogWarning(
                    "Outbox claim {ClaimToken} for message {MessageId} expired before completion in {ServiceName}.",
                    claimToken,
                    message.Id,
                    _options.ServiceName);
            }
        }

        return publishedCount;
    }

    private async Task<OutboxMessage?> ClaimNextAsync(
        TContext context,
        Guid claimToken,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var claimedUntil = now.Add(_options.ClaimLeaseDuration);

        if (!context.Database.IsRelational())
        {
            var pending = await context.Set<OutboxMessage>()
                .Where(message => message.PublishedAtUtc == null)
                .OrderBy(message => message.AggregateType)
                .ThenBy(message => message.AggregateId)
                .ThenBy(message => message.AggregateSequence)
                .ThenBy(message => message.OccurredAtUtc)
                .ThenBy(message => message.Id)
                .ToListAsync(cancellationToken);
            var message = pending
                .GroupBy(item => new { item.AggregateType, item.AggregateId })
                .Select(group => group.First())
                .FirstOrDefault(item =>
                    (item.NextAttemptAtUtc is null || item.NextAttemptAtUtc <= now) &&
                    (item.ClaimedUntilUtc is null || item.ClaimedUntilUtc <= now));

            if (message is null)
            {
                return null;
            }

            message.ClaimToken = claimToken;
            message.ClaimedUntilUtc = claimedUntil;
            await context.SaveChangesAsync(cancellationToken);
            return message;
        }

        if (!string.Equals(
                context.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            throw new NotSupportedException("Atomic outbox claims require the PostgreSQL provider.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var candidates = await context.Set<OutboxMessage>()
            .FromSqlInterpolated($"""
                SELECT candidate.*
                FROM "OutboxMessages" AS candidate
                WHERE candidate."PublishedAtUtc" IS NULL
                  AND (candidate."NextAttemptAtUtc" IS NULL OR candidate."NextAttemptAtUtc" <= {now})
                  AND (candidate."ClaimedUntilUtc" IS NULL OR candidate."ClaimedUntilUtc" <= {now})
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "OutboxMessages" AS earlier
                      WHERE earlier."PublishedAtUtc" IS NULL
                        AND earlier."AggregateType" = candidate."AggregateType"
                        AND earlier."AggregateId" = candidate."AggregateId"
                        AND (
                            earlier."AggregateSequence" < candidate."AggregateSequence"
                            OR (
                                earlier."AggregateSequence" = candidate."AggregateSequence"
                                AND earlier."OccurredAtUtc" < candidate."OccurredAtUtc"
                            )
                            OR (
                                earlier."AggregateSequence" = candidate."AggregateSequence"
                                AND earlier."OccurredAtUtc" = candidate."OccurredAtUtc"
                                AND earlier."Id" < candidate."Id"
                            )
                        )
                  )
                ORDER BY candidate."AggregateType",
                         candidate."AggregateId",
                         candidate."AggregateSequence",
                         candidate."OccurredAtUtc",
                         candidate."Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        var claimedMessage = candidates.SingleOrDefault();

        if (claimedMessage is not null)
        {
            claimedMessage.ClaimToken = claimToken;
            claimedMessage.ClaimedUntilUtc = claimedUntil;
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return claimedMessage;
    }

    private async Task<bool> MarkPublishedAsync(
        TContext context,
        OutboxMessage message,
        Guid claimToken,
        CancellationToken cancellationToken)
    {
        var publishedAt = DateTime.UtcNow;
        if (!context.Database.IsRelational())
        {
            if (message.ClaimToken != claimToken || message.PublishedAtUtc is not null)
            {
                return false;
            }

            message.PublishedAtUtc = publishedAt;
            message.LastError = null;
            message.NextAttemptAtUtc = null;
            message.ClaimToken = null;
            message.ClaimedUntilUtc = null;
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        var updated = await context.Set<OutboxMessage>()
            .Where(item =>
                item.Id == message.Id &&
                item.PublishedAtUtc == null &&
                item.ClaimToken == claimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PublishedAtUtc, publishedAt)
                .SetProperty(item => item.LastError, (string?)null)
                .SetProperty(item => item.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(item => item.ClaimToken, (Guid?)null)
                .SetProperty(item => item.ClaimedUntilUtc, (DateTime?)null),
                cancellationToken);
        return updated == 1;
    }

    private async Task MarkFailedAsync(
        TContext context,
        OutboxMessage message,
        Guid claimToken,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var attempts = message.PublishAttempts + 1;
        var nextAttempt = DateTime.UtcNow.Add(RetryDelay(attempts));
        var error = exception.Message.Length <= 2000
            ? exception.Message
            : exception.Message[..2000];
        bool released;

        if (!context.Database.IsRelational())
        {
            released = message.ClaimToken == claimToken && message.PublishedAtUtc is null;
            if (released)
            {
                message.PublishAttempts = attempts;
                message.LastError = error;
                message.NextAttemptAtUtc = nextAttempt;
                message.ClaimToken = null;
                message.ClaimedUntilUtc = null;
                await context.SaveChangesAsync(cancellationToken);
            }
        }
        else
        {
            released = await context.Set<OutboxMessage>()
                .Where(item =>
                    item.Id == message.Id &&
                    item.PublishedAtUtc == null &&
                    item.ClaimToken == claimToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.PublishAttempts, item => item.PublishAttempts + 1)
                    .SetProperty(item => item.LastError, error)
                    .SetProperty(item => item.NextAttemptAtUtc, nextAttempt)
                    .SetProperty(item => item.ClaimToken, (Guid?)null)
                    .SetProperty(item => item.ClaimedUntilUtc, (DateTime?)null),
                    cancellationToken) == 1;
        }

        if (released)
        {
            _logger.LogWarning(
                exception,
                "Outbox publish failed for {MessageId} from {ServiceName}; the committed event remains pending.",
                message.Id,
                _options.ServiceName);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "Outbox publish failed after claim {ClaimToken} for {MessageId} was lost in {ServiceName}.",
                claimToken,
                message.Id,
                _options.ServiceName);
        }
    }

    private static TimeSpan RetryDelay(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempts, 5))));
}
