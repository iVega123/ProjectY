using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using ProjectY.Shared.Pagination;
using RentalOperations.Domain;
using RentalOperations.Model;
using RentalCore.Errors;

namespace RentalOperations.Repository;

/// <summary>
/// Aluguéis na mesma transação do outbox que os anuncia.
///
/// Esta é a promessa do ADR 0009 cumprida em vez de aproximada: a linha do
/// aluguel e a linha do evento entram juntas ou não entram. Antes o aluguel
/// ficava no MongoDB e o evento numa lista dentro do próprio documento -- o que
/// já era melhor do que publicar direto, mas dependia de o publicador voltar ao
/// documento certo. Agora é uma transação do banco.
///
/// A dupla reserva também mudou de dono. Não há checagem prévia decidindo se a
/// moto está livre: quem recusa é o índice único parcial. Duas requisições
/// simultâneas chegam ao INSERT, uma passa, a outra recebe violação de
/// unicidade -- que é a única forma de a garantia valer sob concorrência real.
///
/// A escrita trava a linha da moto (FOR UPDATE) em vez de só consultá-la. O
/// índice parcial cuida de dois aluguéis disputando a mesma moto, mas não do
/// aluguel disputando com a aposentadoria: aquilo é write skew, e sob READ
/// COMMITTED os dois enxergariam um mundo em que ambos podem prosseguir. O
/// CockroachDB é serializável e recusaria sozinho; o PostgreSQL não, e o mesmo
/// código roda nos dois. A trava faz a segunda escrita esperar e reavaliar a
/// condição -- que é a corrida de retirada do ADR 0010, decidida pelo banco.
/// </summary>
public sealed class SqlRentalRepository(NpgsqlDataSource database) : IRentalRepository
{
    private const string ActiveRentalIndex = "one_active_rental_per_motorcycle";

    private const string SelectRental = """
        SELECT r.id, r.rider_id, r.motorcycle_id, r.rider_name, m.license_plate,
               r.starts_at, r.predicted_ends_at, r.ends_at, r.init_cost, r.status, r.created_at
          FROM rentals AS r
          JOIN motorcycles AS m ON m.id = r.motorcycle_id
        """;

    public async Task<Rental> CreateRentalAsync(Rental rental, CancellationToken token = default)
    {
        if (rental.Id == Guid.Empty)
        {
            rental.Id = Guid.NewGuid();
        }

        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        try
        {
            int inserted;
            await using (var insert = new NpgsqlCommand("""
                WITH available AS (
                    SELECT id FROM motorcycles
                     WHERE id = @motorcycle AND retired_at IS NULL
                     FOR UPDATE
                )
                INSERT INTO rentals (id, rider_id, motorcycle_id, rider_name, starts_at,
                                     predicted_ends_at, ends_at, init_cost, status)
                SELECT @id, @rider, available.id, @rider_name, @starts::timestamptz,
                       @predicted::timestamptz, @ends::timestamptz, @init::decimal, @status
                  FROM available
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("id", rental.Id);
                insert.Parameters.AddWithValue("rider", rental.UserId);
                insert.Parameters.AddWithValue("motorcycle", rental.MotorcycleId);
                insert.Parameters.AddWithValue("rider_name", (object?)rental.RiderName ?? DBNull.Value);
                insert.Parameters.AddWithValue("starts", Utc(rental.StartDate));
                insert.Parameters.AddWithValue("predicted", Utc(rental.PredictedEndDate));
                insert.Parameters.AddWithValue("ends", rental.EndDate is { } ends ? Utc(ends) : DBNull.Value);
                insert.Parameters.AddWithValue("init", rental.InitCost);
                insert.Parameters.AddWithValue("status", ToColumn(rental.Status));
                inserted = await insert.ExecuteNonQueryAsync(token);
            }

            if (inserted == 0)
            {
                await transaction.RollbackAsync(token);
                throw await RefusedAsync(rental, token);
            }

            await EnqueueAsync(connection, transaction, rental, "rental.started", token);
            await transaction.CommitAsync(token);
            return rental;
        }
        catch (PostgresException conflict) when (IsActiveRentalConflict(conflict))
        {
            await transaction.RollbackAsync(token);
            throw new ActiveRentalConflictException(rental.MotorcycleId, conflict);
        }
        catch (PostgresException missing)
            when (missing.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            await transaction.RollbackAsync(token);
            throw new ResourceNotFoundException("The motorcycle does not exist.");
        }
    }

    public async Task UpdateRentalAsync(Rental rental, CancellationToken token = default)
    {
        var closing = rental.Status == RentalStatus.Completed;
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);

        // Fechar é uma transição, não uma sobrescrita. A condição de status é o
        // que faz duas liquidações concorrentes terminarem com um vencedor e um
        // conflito relatado, em vez de dois eventos rental.closed para o mesmo
        // aluguel.
        var guard = closing ? " AND status <> 'closed'" : string.Empty;
        int updated;
        await using (var update = new NpgsqlCommand($"""
            UPDATE rentals
               SET ends_at = @ends,
                   status = @status
             WHERE id = @id{guard}
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", rental.Id);
            update.Parameters.AddWithValue("ends", rental.EndDate is { } ends ? Utc(ends) : DBNull.Value);
            update.Parameters.AddWithValue("status", ToColumn(rental.Status));
            updated = await update.ExecuteNonQueryAsync(token);
        }

        if (updated == 0)
        {
            await transaction.RollbackAsync(token);
            throw new RentalSettlementConflictException();
        }

        if (closing)
        {
            await EnqueueAsync(connection, transaction, rental, "rental.closed", token);
        }

        await transaction.CommitAsync(token);
    }

    public async Task<Rental?> GetRentalByIdAsync(string id, CancellationToken token = default)
    {
        if (!Guid.TryParse(id, out var rentalId))
        {
            return null;
        }

        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(SelectRental + " WHERE r.id = @id", connection);
        command.Parameters.AddWithValue("id", rentalId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    /// <summary>
    /// Um SELECT para muitos ids, em vez de um SELECT por id.
    ///
    /// O tamanho do lote é limitado antes de chegar aqui; o `= ANY` recebe um
    /// arranjo, e não uma lista de parâmetros montada por concatenação, para que
    /// o plano seja o mesmo qualquer que seja a quantidade de ids -- e para que
    /// não haja string de SQL sendo construída a partir de entrada do cliente.
    ///
    /// Ids desconhecidos simplesmente não voltam. Quem pediu compara o que
    /// recebeu com o que pediu; o banco não precisa opinar sobre a diferença.
    /// </summary>
    public async Task<IReadOnlyList<Rental>> GetRentalsByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken token = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(
            SelectRental + " WHERE r.id = ANY(@ids) ORDER BY r.created_at DESC, r.id DESC",
            connection);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = ids.ToArray()
        });

        var rentals = new List<Rental>(ids.Count);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rentals.Add(Read(reader));
        }

        return rentals;
    }

    public async Task<CursorPage<Rental>> GetRentalsByUserId(
        string userId,
        string? cursor,
        int? pageSize,
        CancellationToken token = default)
    {
        var size = CursorPagination.NormalizePageSize(pageSize);
        var position = Position.Parse(CursorPagination.Decode(cursor));

        // A ordem segue o índice rentals_by_rider: o mais recente primeiro, com
        // o id desempatando para que dois aluguéis criados no mesmo instante não
        // troquem de lugar entre uma página e a seguinte.
        var keyset = position is null
            ? string.Empty
            : " AND (r.created_at, r.id) < (@after_at, @after_id)";
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand($"""
            {SelectRental}
             WHERE r.rider_id = @rider{keyset}
             ORDER BY r.created_at DESC, r.id DESC
             LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("rider", userId);
        command.Parameters.AddWithValue("limit", size + 1);
        if (position is { } after)
        {
            command.Parameters.AddWithValue("after_at", after.CreatedAt);
            command.Parameters.AddWithValue("after_id", after.Id);
        }

        var fetched = new List<Rental>();
        var positions = new Dictionary<Guid, DateTime>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            var createdAt = -1;
            while (await reader.ReadAsync(token))
            {
                if (createdAt < 0)
                {
                    createdAt = reader.GetOrdinal("created_at");
                }

                var rental = Read(reader);
                positions[rental.Id] = reader.GetDateTime(createdAt);
                fetched.Add(rental);
            }
        }

        return CursorPagination.CreatePage(
            fetched,
            size,
            rental => new Position(positions[rental.Id], rental.Id).ToCursor());
    }

    public async Task<bool> HasOverlappingRentalAsync(
        Guid motorcycleId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken token = default)
    {
        // Um aluguel devolvido antes da hora continua ocupando a agenda até a
        // data prevista; por isso a ponta comparada é ends_at quando existe e
        // predicted_ends_at quando não.
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT 1
              FROM rentals
             WHERE motorcycle_id = @motorcycle
               AND status IN ('active', 'closed')
               AND starts_at < @ends
               AND COALESCE(ends_at, predicted_ends_at) > @starts
             LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("motorcycle", motorcycleId);
        command.Parameters.AddWithValue("starts", Utc(startDate));
        command.Parameters.AddWithValue("ends", Utc(endDate));
        return await command.ExecuteScalarAsync(token) is not null;
    }

    public async Task<bool> IsMotorcycleCurrentlyRentedAsync(
        Guid motorcycleId,
        CancellationToken token = default)
    {
        // A junção com motorcycles saiu junto com a placa: a pergunta é sobre o
        // id, que a própria tabela de aluguéis já guarda.
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT 1
              FROM rentals
             WHERE motorcycle_id = @motorcycle
               AND status = 'active'
               AND starts_at <= now()
               AND predicted_ends_at >= now()
             LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("motorcycle", motorcycleId);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>
    /// Por que a moto recusou o aluguel: ela não existe, ou foi aposentada.
    ///
    /// A pergunta só é feita quando o INSERT não escreveu nada, que é o caminho
    /// raro. Vale a segunda ida ao banco porque as duas respostas dizem coisas
    /// diferentes a quem pediu -- uma é erro de requisição, a outra é um estado
    /// do domínio que o cliente precisa reconhecer.
    /// </summary>
    private async Task<Exception> RefusedAsync(Rental rental, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM motorcycles WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", rental.MotorcycleId);
        return await command.ExecuteScalarAsync(token) is not null
            ? new MotorcycleRetiredException(rental.MotorcycleId)
            : new ArgumentException("Motorcycle does not exist.");
    }

    private static async Task EnqueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Rental rental,
        string topic,
        CancellationToken token)
    {
        var envelope = RentalEventEnvelope.Create(rental, topic);
        await using var command = new NpgsqlCommand("""
            INSERT INTO outbox (aggregate_type, aggregate_id, event_type, topic, payload, trace_parent)
            VALUES ('rental', @aggregate, @event_type, @topic, @payload, @trace)
            """, connection, transaction);
        command.Parameters.AddWithValue("aggregate", RentalEventEnvelope.PartitionKey(rental));
        command.Parameters.AddWithValue("event_type", envelope.Id);
        command.Parameters.AddWithValue("topic", envelope.Topic);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Bytea) { Value = envelope.Payload });
        command.Parameters.AddWithValue("trace", (object?)envelope.TraceParent ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static bool IsActiveRentalConflict(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.UniqueViolation &&
        (exception.ConstraintName is null ||
         exception.ConstraintName.Contains(ActiveRentalIndex, StringComparison.Ordinal));

    private static Rental Read(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(reader.GetOrdinal("id")),
        UserId = reader.GetString(reader.GetOrdinal("rider_id")),
        MotorcycleId = reader.GetGuid(reader.GetOrdinal("motorcycle_id")),
        RiderName = Optional(reader, "rider_name"),
        MotorcycleLicencePlate = reader.GetString(reader.GetOrdinal("license_plate")),
        StartDate = reader.GetDateTime(reader.GetOrdinal("starts_at")),
        PredictedEndDate = reader.GetDateTime(reader.GetOrdinal("predicted_ends_at")),
        EndDate = reader.IsDBNull(reader.GetOrdinal("ends_at"))
            ? null
            : reader.GetDateTime(reader.GetOrdinal("ends_at")),
        InitCost = reader.GetDecimal(reader.GetOrdinal("init_cost")),
        Status = FromColumn(reader.GetString(reader.GetOrdinal("status")))
    };

    private static string? Optional(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    public static string ToColumn(RentalStatus status) => status switch
    {
        RentalStatus.Active => "active",
        RentalStatus.Completed => "closed",
        RentalStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No column value for this status.")
    };

    public static RentalStatus FromColumn(string value) => value switch
    {
        "active" => RentalStatus.Active,
        "closed" => RentalStatus.Completed,
        "cancelled" => RentalStatus.Cancelled,
        _ => throw new InvalidDataException($"Unknown rental status '{value}'.")
    };

    /// <summary>A posição na página: o instante de criação e o id que o desempata.</summary>
    private readonly record struct Position(DateTime CreatedAt, Guid Id)
    {
        public string ToCursor() =>
            CreatedAt.ToString("O", CultureInfo.InvariantCulture) + "|" + Id.ToString("D");

        public static Position? Parse(string? cursor)
        {
            if (cursor is null)
            {
                return null;
            }

            var separator = cursor.IndexOf('|', StringComparison.Ordinal);
            if (separator <= 0 ||
                !DateTime.TryParse(
                    cursor[..separator],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var createdAt) ||
                !Guid.TryParse(cursor[(separator + 1)..], out var id))
            {
                throw new FormatException("The pagination cursor is invalid.");
            }

            return new Position(
                createdAt.Kind == DateTimeKind.Utc
                    ? createdAt
                    : DateTime.SpecifyKind(createdAt.ToUniversalTime(), DateTimeKind.Utc),
                id);
        }
    }
}
