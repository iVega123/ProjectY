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
            await DispatchOnceAsync(stoppingToken);
            await Task.Delay(_options.PollInterval, stoppingToken);
        }
    }

    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = DateTime.UtcNow;
        var messages = await context.Set<OutboxMessage>()
            .Where(message => message.PublishedAtUtc == null)
            .OrderBy(message => message.AggregateType)
            .ThenBy(message => message.AggregateId)
            .ThenBy(message => message.AggregateSequence)
            .ThenBy(message => message.OccurredAtUtc)
            .ThenBy(message => message.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);
        var blockedAggregates = new HashSet<string>(StringComparer.Ordinal);
        var publishedCount = 0;

        foreach (var message in messages)
        {
            var aggregateKey = $"{message.AggregateType}\u001f{message.AggregateId}";
            if (blockedAggregates.Contains(aggregateKey))
            {
                continue;
            }

            if (message.NextAttemptAtUtc > now)
            {
                blockedAggregates.Add(aggregateKey);
                continue;
            }

            try
            {
                await _transport.PublishAsync(message, cancellationToken);
                message.PublishedAtUtc = DateTime.UtcNow;
                message.LastError = null;
                message.NextAttemptAtUtc = null;
                await context.SaveChangesAsync(cancellationToken);
                publishedCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                message.PublishAttempts++;
                message.LastError = exception.Message;
                message.NextAttemptAtUtc = DateTime.UtcNow.Add(RetryDelay(message.PublishAttempts));
                await context.SaveChangesAsync(cancellationToken);
                blockedAggregates.Add(aggregateKey);
                _logger.LogWarning(
                    exception,
                    "Outbox publish failed for {MessageId} from {ServiceName}; the committed event remains pending.",
                    message.Id,
                    _options.ServiceName);
            }
        }

        return publishedCount;
    }

    private static TimeSpan RetryDelay(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempts, 5))));
}
