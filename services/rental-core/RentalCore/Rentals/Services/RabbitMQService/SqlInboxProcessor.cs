using Npgsql;

namespace RentalOperations.Services.RabbitMQService;

public sealed class InboxOptions
{
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
}

public sealed class InboxMessageInProgressException(string messageId)
    : Exception($"Inbox message {messageId} is already being processed.");

/// <summary>
/// Deduplicação no consumo, na tabela <c>inbox</c> do schema alvo.
///
/// A reserva tem prazo por um motivo específico: a réplica que morre no meio do
/// tratamento não pode bloquear a mensagem para sempre, mas também não pode
/// liberá-la de imediato, ou uma segunda réplica trataria a mesma mensagem em
/// paralelo. O prazo é o que separa "está sendo tratada" de "ficou pendurada".
/// </summary>
public sealed class SqlInboxProcessor(
    NpgsqlDataSource database,
    InboxOptions options,
    TimeProvider timeProvider)
{
    public const string CompletedStatus = "completed";
    private const string ProcessingStatus = "processing";

    public async Task<bool> ProcessAsync(
        string messageId,
        string consumerName,
        Func<CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentNullException.ThrowIfNull(handler);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var claimToken = Guid.NewGuid().ToString("D");

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        object? claimed;
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO inbox (message_id, consumer, handled_at, status, claim_token, claimed_until)
            VALUES (@message, @consumer, @now, @processing, @token, @until)
            ON CONFLICT (message_id, consumer) DO UPDATE
               SET status = @processing,
                   claim_token = @token,
                   claimed_until = @until
             WHERE inbox.status <> @completed
               AND (inbox.claimed_until IS NULL OR inbox.claimed_until < @now)
            RETURNING claim_token
            """, connection))
        {
            claim.Parameters.AddWithValue("message", messageId);
            claim.Parameters.AddWithValue("consumer", consumerName);
            claim.Parameters.AddWithValue("now", now);
            claim.Parameters.AddWithValue("processing", ProcessingStatus);
            claim.Parameters.AddWithValue("completed", CompletedStatus);
            claim.Parameters.AddWithValue("token", claimToken);
            claim.Parameters.AddWithValue("until", now + options.ClaimLease);
            claimed = await claim.ExecuteScalarAsync(cancellationToken);
        }

        if (claimed is null)
        {
            // Ou já terminou -- e então a repetição é um não-evento, que é o
            // ponto do inbox -- ou outra réplica está com a reserva válida.
            await using var existing = new NpgsqlCommand(
                "SELECT status FROM inbox WHERE message_id = @message AND consumer = @consumer",
                connection);
            existing.Parameters.AddWithValue("message", messageId);
            existing.Parameters.AddWithValue("consumer", consumerName);
            var status = await existing.ExecuteScalarAsync(cancellationToken) as string;
            return status == CompletedStatus
                ? false
                : throw new InboxMessageInProgressException(messageId);
        }

        try
        {
            await handler(cancellationToken);
            await using var complete = new NpgsqlCommand("""
                UPDATE inbox
                   SET status = @completed,
                       handled_at = @now,
                       claim_token = NULL,
                       claimed_until = NULL
                 WHERE message_id = @message
                   AND consumer = @consumer
                   AND claim_token = @token
                """, connection);
            complete.Parameters.AddWithValue("message", messageId);
            complete.Parameters.AddWithValue("consumer", consumerName);
            complete.Parameters.AddWithValue("token", claimToken);
            complete.Parameters.AddWithValue("completed", CompletedStatus);
            complete.Parameters.AddWithValue("now", timeProvider.GetUtcNow().UtcDateTime);
            return await complete.ExecuteNonQueryAsync(cancellationToken) == 1
                ? true
                : throw new InboxMessageInProgressException(messageId);
        }
        catch
        {
            // A reserva é devolvida imediatamente: o tratamento falhou, e a
            // próxima entrega deve poder tentar de novo sem esperar o prazo.
            await using var release = new NpgsqlCommand("""
                UPDATE inbox
                   SET claim_token = NULL,
                       claimed_until = @epoch
                 WHERE message_id = @message
                   AND consumer = @consumer
                   AND claim_token = @token
                """, connection);
            release.Parameters.AddWithValue("message", messageId);
            release.Parameters.AddWithValue("consumer", consumerName);
            release.Parameters.AddWithValue("token", claimToken);
            release.Parameters.AddWithValue("epoch", DateTime.UnixEpoch);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
            throw;
        }
    }
}

/// <summary>
/// Apaga as entradas velhas do inbox.
///
/// No MongoDB isto era um índice TTL, que o próprio servidor varria. O schema
/// alvo não tem TTL, e um inbox que só cresce acaba custando mais do que a
/// duplicata que ele evita -- então a varredura passa a ser explícita, com o
/// mesmo período de retenção de antes.
/// </summary>
public sealed class InboxRetentionSweeper(
    NpgsqlDataSource database,
    InboxOptions options,
    ILogger<InboxRetentionSweeper> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var removed = await SweepAsync(database, options.RetentionPeriod, token);
                if (removed > 0)
                {
                    log.LogDebug("Removed {Count} expired inbox entries", removed);
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                log.LogWarning(error, "Inbox retention sweep delayed; retrying on the next pass");
            }

            await Task.Delay(Interval, token);
        }
    }

    public static async Task<int> SweepAsync(
        NpgsqlDataSource database,
        TimeSpan retention,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(
            "DELETE FROM inbox WHERE handled_at < @cutoff AND status = 'completed'", connection);
        command.Parameters.AddWithValue("cutoff", DateTime.UtcNow - retention);
        return await command.ExecuteNonQueryAsync(token);
    }
}
