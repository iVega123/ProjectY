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
        Task DeleteRentalAsync(string id);
    }
}
