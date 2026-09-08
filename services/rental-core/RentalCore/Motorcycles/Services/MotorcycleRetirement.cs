using Npgsql;

namespace MotoHub.Services;

/// <summary>O que aconteceu com o pedido de aposentadoria.</summary>
public enum MotorcycleRetirementResult
{
    Retired,
    AlreadyRetired,
    ActiveRental
}

public interface IMotorcycleRetirement
{
    Task<MotorcycleRetirementResult> RetireAsync(
        Guid motorcycleId,
        DateTime retiredAtUtc,
        string reason,
        CancellationToken token = default);
}

/// <summary>
/// Aposentar uma moto, numa transação, com a linha da moto travada.
///
/// Isto era um protocolo entre dois serviços: o MotoHub pedia ao
/// RentalOperations que reservasse uma marca de aposentadoria no MongoDB e, se
/// conseguisse, apagava a moto no Postgres. Entre os dois passos havia uma
/// janela, e a marca reservada precisava ficar para trás numa falha ambígua
/// justamente porque ninguém conseguia desfazer os dois lados juntos.
///
/// Com aluguéis e motos no mesmo banco, a moto é o ponto de encontro: quem
/// aposenta e quem aluga travam a mesma linha, e por isso um espera o outro. A
/// ordem dos comandos importa e não é acidental -- travar, e só então perguntar
/// se há aluguel ativo. Sob READ COMMITTED cada comando enxerga um instantâneo
/// novo, então a pergunta feita depois da trava vê o aluguel que acabou de ser
/// gravado; a mesma pergunta embutida no WHERE do UPDATE não veria, porque a
/// reavaliação do PostgreSQL reusa o instantâneo do comando. É a corrida de
/// retirada do ADR 0010, decidida pelo banco em vez de negociada.
/// </summary>
public sealed class MotorcycleRetirement(NpgsqlDataSource database) : IMotorcycleRetirement
{
    public async Task<MotorcycleRetirementResult> RetireAsync(
        Guid motorcycleId,
        DateTime retiredAtUtc,
        string reason,
        CancellationToken token = default)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);

        bool retired;
        await using (var claim = new NpgsqlCommand(
            "SELECT retired_at IS NOT NULL FROM motorcycles WHERE id = @id FOR UPDATE",
            connection, transaction))
        {
            claim.Parameters.AddWithValue("id", motorcycleId);
            if (await claim.ExecuteScalarAsync(token) is not bool alreadyRetired)
            {
                await transaction.RollbackAsync(token);
                throw new InvalidOperationException($"Motorcycle {motorcycleId} does not exist.");
            }

            retired = alreadyRetired;
        }

        if (retired)
        {
            await transaction.RollbackAsync(token);
            return MotorcycleRetirementResult.AlreadyRetired;
        }

        await using (var rented = new NpgsqlCommand(
            "SELECT 1 FROM rentals WHERE motorcycle_id = @id AND status = 'active' LIMIT 1",
            connection, transaction))
        {
            rented.Parameters.AddWithValue("id", motorcycleId);
            if (await rented.ExecuteScalarAsync(token) is not null)
            {
                await transaction.RollbackAsync(token);
                return MotorcycleRetirementResult.ActiveRental;
            }
        }

        await using (var retire = new NpgsqlCommand("""
            UPDATE motorcycles
               SET retired_at = @at, retirement_reason = @reason
             WHERE id = @id AND retired_at IS NULL
            """, connection, transaction))
        {
            retire.Parameters.AddWithValue("id", motorcycleId);
            retire.Parameters.AddWithValue("at", DateTime.SpecifyKind(retiredAtUtc, DateTimeKind.Utc));
            retire.Parameters.AddWithValue("reason", reason);
            await retire.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
        return MotorcycleRetirementResult.Retired;
    }
}
