using RentalOperations.Domain;
using RentalOperations.Model;

namespace RentalOperations.Repository
{
    public interface IRentalRepository
    {
        Task<Rental> CreateRentalAsync(Rental rental);
        Task<Rental> GetRentalByIdAsync(string id);
        Task<List<Rental>> GetRentalsByUserId(string userId);
        Task<List<Rental>> GetRentalsByMotorcycleIdAsync(string licencePlate);
        Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate);
        Task UpdateRentalAsync(Rental rental);
        Task UpdateLicensePlateForAllRentalsAsync(string oldLicensePlate, string newLicensePlate);
        Task<MotorcycleClaimResult> TryClaimRentalAsync(string licencePlate, string rentalId);
        Task<MotorcycleClaimResult> TryClaimRetirementAsync(string licencePlate);
        Task ReleaseRentalClaimAsync(string licencePlate, string rentalId);
        Task DeleteRentalAsync(string id);
    }
}
