using System.Diagnostics.Metrics;
using MongoDB.Bson;
using Npgsql;
using RentalOperations.Data;
using RentalOperations.Model;

namespace RentalOperations.Repository;

/// <summary>
/// The one place a rental is expressed as a target-schema row.
///
/// The data source is injected rather than built here: each one owns its own
/// connection pool, so creating one per write would return each connection to a
/// pool nobody uses again and open a fresh physical connection for the next
/// write. Under sustained traffic that exhausts the engine, not the process.
/// </summary>
public sealed class RentalTargetWriter(NpgsqlDataSource target, ILogger<RentalTargetWriter> log)
{
    private static readonly Meter Meter = new("ProjectY.Migration");
    private static readonly Counter<long> Written =
        Meter.CreateCounter<long>("projecty.rentals.dual_write.applied");
    private static readonly Counter<long> Diverged =
        Meter.CreateCounter<long>("projecty.rentals.dual_write.diverged");
    private static readonly Counter<long> Reconciled =
        Meter.CreateCounter<long>("projecty.rentals.dual_write.reconciled");

    public async Task<bool> MirrorAsync(Rental rental, CancellationToken token = default)
    {
        if (RentalRowMapper.Refusal(rental) is { } refusal)
        {
            Record(refusal.RentalId, refusal.Reason);
            return false;
        }
        RentalRowMapper.TryMapStatus(rental.Status, out var status);
        try
        {
            await using var connection = await target.OpenConnectionAsync(token);
            await using var command = new NpgsqlCommand("""
                INSERT INTO rentals (id, rider_id, motorcycle_id, starts_at, predicted_ends_at,
                                     ends_at, init_cost, final_cost, status)
                VALUES (@id, @rider, @motorcycle, @starts, @predicted, @ends, @init, @final, @status)
                ON CONFLICT (id) DO UPDATE SET
                    ends_at = EXCLUDED.ends_at,
                    final_cost = EXCLUDED.final_cost,
                    status = EXCLUDED.status
                """, connection);
            command.Parameters.AddWithValue("id", RentalRowMapper.ToRowId(rental._id!.Value));
            command.Parameters.AddWithValue("rider", rental.UserId);
            command.Parameters.AddWithValue("motorcycle", Guid.Parse(rental.MotorcycleId));
            command.Parameters.AddWithValue("starts", DateTime.SpecifyKind(rental.StartDate, DateTimeKind.Utc));
            command.Parameters.AddWithValue("predicted", DateTime.SpecifyKind(rental.PredictedEndDate, DateTimeKind.Utc));
            command.Parameters.AddWithValue("ends", rental.EndDate is { } ends
                ? DateTime.SpecifyKind(ends, DateTimeKind.Utc) : DBNull.Value);
            command.Parameters.AddWithValue("init", rental.InitCost);
            command.Parameters.AddWithValue("final", rental.FinalCost == 0m ? DBNull.Value : rental.FinalCost);
            command.Parameters.AddWithValue("status", status);
            await command.ExecuteNonQueryAsync(token);
            Written.Add(1);
            return true;
        }
        catch (Exception error)
        {
            Record(rental._id!.Value.ToString(), error.Message);
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken token = default)
    {
        try
        {
            await using var connection = await target.OpenConnectionAsync(token);
            await using var command = new NpgsqlCommand("DELETE FROM rentals WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", RentalRowMapper.ToRowId(ObjectId.Parse(id)));
            await command.ExecuteNonQueryAsync(token);
            Written.Add(1);
            return true;
        }
        catch (Exception error)
        {
            Record(id, "delete failed: " + error.Message);
            return false;
        }
    }

    /// <summary>Which of these rentals already have a row, so the rest can be retried.</summary>
    public async Task<HashSet<Guid>> PresentAsync(IReadOnlyCollection<Guid> ids, CancellationToken token)
    {
        var present = new HashSet<Guid>();
        if (ids.Count == 0) return present;
        await using var connection = await target.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(
            "SELECT id FROM rentals WHERE id = ANY(@ids)", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) present.Add(reader.GetGuid(0));
        return present;
    }

    public void CountReconciled() => Reconciled.Add(1);

    // Named, counted, and at warning level. A divergence is a row the cutover
    // cannot be declared over, so it has to survive being looked at once.
    private void Record(string rentalId, string reason)
    {
        Diverged.Add(1);
        log.LogWarning("Rental {RentalId} did not reach the target schema: {Reason}", rentalId, reason);
    }
}
