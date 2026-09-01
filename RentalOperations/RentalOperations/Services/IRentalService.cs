using RentalOperations.DTOs;
using RentalOperations.Model;

using ProjectY.Shared.Pagination;

namespace RentalOperations.Services
{
    public interface IRentalService
    {
        Task CreateRentalAsync(RentalCreateDto createDto, string userId);
        Task<ResponseRentalDTO> CalculateFinalCostAsync(string rentalId, string userId, DateTime actualEndDate);
        Task<CursorPage<ResponseRentalDTO>> GetRentalsByUserIdAsync(
            string userId,
            string? cursor,
            int? pageSize);
        Task UpdateMotorcycleLicensePlateAsync(string oldLicensePlate, string newLicensePlate);
        Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate);
        Task<bool> TryRetireMotorcycleAsync(string licencePlate);
    }
}

