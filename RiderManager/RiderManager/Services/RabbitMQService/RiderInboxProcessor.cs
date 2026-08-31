using Microsoft.EntityFrameworkCore;
using RiderManager.Data;

namespace RiderManager.Services.RabbitMQService;

public interface IRiderInboxProcessor
{
    Task<bool> ProcessAsync(
        string messageId,
        string consumerName,
        Func<CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);
}

public sealed class RiderInboxProcessor : IRiderInboxProcessor
{
    private readonly ApplicationDbContext _context;

    public RiderInboxProcessor(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> ProcessAsync(
        string messageId,
        string consumerName,
        Func<CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentNullException.ThrowIfNull(handler);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var inserted = await _context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "InboxMessages" ("MessageId", "ConsumerName", "ProcessedAtUtc")
            VALUES ({{messageId}}, {{consumerName}}, {{DateTime.UtcNow}})
            ON CONFLICT ("MessageId", "ConsumerName") DO NOTHING
            """, cancellationToken);

        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await handler(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
