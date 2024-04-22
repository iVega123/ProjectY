using RentalOperations.DTOs;
using RentalOperations.Model;

namespace RentalOperations.Services
{
    public interface IRentalService
    {
        Task CreateRentalAsync(RentalCreateDto createDto, string userId);
        Task<ResponseRentalDTO> CalculateFinalCostAsync(string rentalId, string userId, DateTime actualEndDate);
        Task<List<ResponseRentalDTO>> GetRentalsByUserIdAsync(string userId);
        Task UpdateMotorcycleLicensePlateAsync(string oldLicensePlate, string newLicensePlate);
        Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate);
    }
}

