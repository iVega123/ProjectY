using Npgsql;
using RentalCoreTests.Integration;
using RentalOperations.Services.RabbitMQService;

namespace RentalCoreTests.Rentals.Integration.Database;

[Collection(RentalCoreDatabaseCollection.Name)]
public sealed class InboxProcessorTests(RentalCoreDatabase database)
{
    private static readonly InboxOptions Options = new()
    {
        ClaimLease = TimeSpan.FromSeconds(5),
        RetentionPeriod = TimeSpan.FromDays(7)
    };

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#inbox-convergence")]
    public async Task SameMessageDeliveredTwice_ExecutesHandlerOnce()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var processor = new SqlInboxProcessor(dataSource, Options, TimeProvider.System);
        var messageId = Guid.NewGuid().ToString("D");
        var effects = 0;

        Task Effect(CancellationToken _)
        {
            effects++;
            return Task.CompletedTask;
        }

        Assert.True(await processor.ProcessAsync(messageId, "test-consumer", Effect));
        Assert.False(await processor.ProcessAsync(messageId, "test-consumer", Effect));
        Assert.Equal(1, effects);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#inbox-convergence")]
    public async Task SameMessageForTwoConsumers_IsHandledByEach()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var processor = new SqlInboxProcessor(dataSource, Options, TimeProvider.System);
        var messageId = Guid.NewGuid().ToString("D");
        var effects = 0;

        Task Effect(CancellationToken _)
        {
            effects++;
            return Task.CompletedTask;
        }

        // A chave é (mensagem, consumidor). Deduplicar só pela mensagem faria um
        // consumidor engolir o trabalho do outro.
        Assert.True(await processor.ProcessAsync(messageId, "consumer-a", Effect));
        Assert.True(await processor.ProcessAsync(messageId, "consumer-b", Effect));
        Assert.Equal(2, effects);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#inbox-convergence")]
    public async Task CrashAfterIdempotentEffect_RedeliveryConvergesAndCompletesInbox()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var processor = new SqlInboxProcessor(dataSource, Options, TimeProvider.System);
        var applications = 0;
        var firstAttempt = true;

        Task IdempotentEffect(CancellationToken _)
        {
            applications++;
            if (firstAttempt)
            {
                firstAttempt = false;
                throw new InvalidOperationException("simulated crash before acknowledgement");
            }

            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync("licence-message", "rental-core/test/v1", IdempotentEffect));
        Assert.True(await processor.ProcessAsync("licence-message", "rental-core/test/v1", IdempotentEffect));

        Assert.Equal(2, applications);
        await using var command = dataSource.CreateCommand(
            "SELECT status FROM inbox WHERE message_id = 'licence-message'");
        Assert.Equal(SqlInboxProcessor.CompletedStatus, await command.ExecuteScalarAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MessageHeldByALiveClaim_IsRefusedRatherThanProcessedTwice()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var processor = new SqlInboxProcessor(dataSource, Options, TimeProvider.System);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var held = processor.ProcessAsync("held-message", "rental-core/test/v1", async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        await Assert.ThrowsAsync<InboxMessageInProgressException>(
            () => processor.ProcessAsync("held-message", "rental-core/test/v1", _ => Task.CompletedTask));

        release.SetResult();
        Assert.True(await held);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#retention-boundaries")]
    public async Task RetentionSweep_RemovesHandledEntriesPastTheirPeriod()
    {
        await database.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using (var seed = dataSource.CreateCommand("""
            INSERT INTO inbox (message_id, consumer, handled_at, status)
            VALUES ('old', 'sweeper-test', now() - INTERVAL '30 days', 'completed'),
                   ('new', 'sweeper-test', now(), 'completed')
            """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        // O índice TTL do MongoDB varria isto sozinho; no schema alvo a varredura
        // é explícita, e por isso precisa de um teste que a veja acontecer.
        var removed = await InboxRetentionSweeper.SweepAsync(
            dataSource, TimeSpan.FromDays(7), CancellationToken.None);

        Assert.Equal(1, removed);
        await using var remaining = dataSource.CreateCommand(
            "SELECT message_id FROM inbox WHERE consumer = 'sweeper-test'");
        Assert.Equal("new", await remaining.ExecuteScalarAsync());
    }
}
