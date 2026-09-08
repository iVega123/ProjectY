using ProjectY.Shared.Pagination;
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
/// failure cannot fail a request. It is counted and named instead, and retried by
/// <see cref="RentalMirrorReconciler"/> -- silence here would turn a dual write
/// into a single write nobody noticed.
/// </summary>
public sealed class DualWriteRentalRepository(
    IRentalRepository inner,
    RentalTargetWriter target) : IRentalRepository
{
    public async Task<Rental> CreateRentalAsync(Rental rental)
    {
        var created = await inner.CreateRentalAsync(rental);
        await target.MirrorAsync(created);
        return created;
    }

    public async Task UpdateRentalAsync(Rental rental)
    {
        await inner.UpdateRentalAsync(rental);
        await target.MirrorAsync(rental);
    }

    public async Task DeleteRentalAsync(string id)
    {
        await inner.DeleteRentalAsync(id);
        await target.DeleteAsync(id);
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
