using System.Diagnostics.Metrics;
using Npgsql;
using ProjectY.Shared.Pagination;
using RentalOperations.Data;
using RentalOperations.Domain;
using RentalOperations.Model;

namespace RentalOperations.Repository;

/// <summary>
/// Writes every rental to both stores and reads from neither but Mongo.
///
/// This is the intermediate step #135 asks for, and the reason it exists is that
/// both uniqueness guarantees have to be live at the same time: the Mongo partial
/// index and one_active_rental_per_motorcycle. A cutover that switched stores in
/// one move would have a window where only one of them held, and the invariant
/// this system rests on is the one that must not have a window.
///
/// Mongo stays authoritative until the comparison is clean, so a target-store
/// failure cannot fail a request. It is counted and named instead: silence here
/// would turn a dual write into a single write nobody noticed.
/// </summary>
public sealed class DualWriteRentalRepository(
    IRentalRepository inner,
    string targetConnectionString,
    ILogger<DualWriteRentalRepository> log) : IRentalRepository
{
    private static readonly Meter Meter = new("ProjectY.Migration");
    private static readonly Counter<long> Written =
        Meter.CreateCounter<long>("projecty.rentals.dual_write.applied");
    private static readonly Counter<long> Diverged =
        Meter.CreateCounter<long>("projecty.rentals.dual_write.diverged");

    public async Task<Rental> CreateRentalAsync(Rental rental)
    {
        var created = await inner.CreateRentalAsync(rental);
        await MirrorAsync(created);
        return created;
    }

    public async Task UpdateRentalAsync(Rental rental)
    {
        await inner.UpdateRentalAsync(rental);
        await MirrorAsync(rental);
    }

    public async Task DeleteRentalAsync(string id)
    {
        await inner.DeleteRentalAsync(id);
        try
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand("DELETE FROM rentals WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", RentalRowMapper.ToRowId(MongoDB.Bson.ObjectId.Parse(id)));
            await command.ExecuteNonQueryAsync();
            Written.Add(1);
        }
        catch (Exception error)
        {
            Record(id, "delete failed: " + error.Message);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var dataSource = NpgsqlDataSource.Create(targetConnectionString);
        return await dataSource.OpenConnectionAsync();
    }

    private async Task MirrorAsync(Rental rental)
    {
        if (RentalRowMapper.Refusal(rental) is { } refusal)
        {
            Record(refusal.RentalId, refusal.Reason);
            return;
        }
        RentalRowMapper.TryMapStatus(rental.Status, out var status);
        try
        {
            await using var connection = await OpenAsync();
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
            await command.ExecuteNonQueryAsync();
            Written.Add(1);
        }
        catch (Exception error)
        {
            Record(rental._id!.Value.ToString(), error.Message);
        }
    }

    // Named, counted, and at warning level. A divergence is a row the cutover
    // cannot be declared over, so it has to survive being looked at once.
    private void Record(string rentalId, string reason)
    {
        Diverged.Add(1);
        log.LogWarning("Rental {RentalId} did not reach the target schema: {Reason}", rentalId, reason);
    }

    public Task<Rental> GetRentalByIdAsync(string id) => inner.GetRentalByIdAsync(id);
    public Task<CursorPage<Rental>> GetRentalsByUserId(string userId, string? cursor, int? pageSize) =>
        inner.GetRentalsByUserId(userId, cursor, pageSize);
    public Task<bool> HasOverlappingRentalAsync(string licencePlate, DateTime startDate, DateTime endDate) =>
        inner.HasOverlappingRentalAsync(licencePlate, startDate, endDate);
    public Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate) =>
        inner.IsMotorcycleCurrentlyRentedAsync(licencePlate);
    public Task UpdateLicensePlateForAllRentalsAsync(string oldLicensePlate, string newLicensePlate) =>
        // The target schema keeps no plate on rentals -- that is the whole point of
        // referencing motorcycle_id -- so a plate correction has nothing to mirror.
        inner.UpdateLicensePlateForAllRentalsAsync(oldLicensePlate, newLicensePlate);
    public Task<bool> TryReserveLicensePlateRenameAsync(string oldLicensePlate, string newLicensePlate) =>
        inner.TryReserveLicensePlateRenameAsync(oldLicensePlate, newLicensePlate);
    public Task<MotorcycleClaimResult> TryClaimRentalAsync(string licencePlate, string rentalId) =>
        inner.TryClaimRentalAsync(licencePlate, rentalId);
    public Task<MotorcycleClaimResult> TryClaimRetirementAsync(string licencePlate) =>
        inner.TryClaimRetirementAsync(licencePlate);
    public Task ReleaseRentalClaimAsync(string licencePlate, string rentalId) =>
        inner.ReleaseRentalClaimAsync(licencePlate, rentalId);
}
