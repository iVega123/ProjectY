using RentalOperations.Domain;
using RentalOperations.Model;

using ProjectY.Shared.Pagination;

namespace RentalOperations.Repository
{
    public interface IRentalRepository
    {
        Task<Rental> CreateRentalAsync(Rental rental);
        Task<Rental> GetRentalByIdAsync(string id);
        Task<CursorPage<Rental>> GetRentalsByUserId(string userId, string? cursor, int? pageSize);
        Task<bool> HasOverlappingRentalAsync(string licencePlate, DateTime startDate, DateTime endDate);
        Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate);
        Task UpdateRentalAsync(Rental rental);
        Task UpdateLicensePlateForAllRentalsAsync(string oldLicensePlate, string newLicensePlate);
        Task<MotorcycleClaimResult> TryClaimRentalAsync(string licencePlate, string rentalId);
        Task<MotorcycleClaimResult> TryClaimRetirementAsync(string licencePlate);
        Task ReleaseRentalClaimAsync(string licencePlate, string rentalId);
        Task DeleteRentalAsync(string id);
    }
}
