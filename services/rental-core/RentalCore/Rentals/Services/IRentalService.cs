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
        Task<IReadOnlyList<ResponseRentalDTO>> GetRentalsByIdsAsync(
            IReadOnlyCollection<Guid> ids,
            string userId,
            bool isAdmin);
        Task<bool> IsMotorcycleCurrentlyRentedAsync(Guid motorcycleId);
    }
}

