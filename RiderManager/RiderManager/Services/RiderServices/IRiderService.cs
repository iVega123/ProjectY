using RiderManager.DTOs;
using RiderManager.Models;

using ProjectY.Shared.Pagination;

namespace RiderManager.Services.RiderServices
{
    public interface IRiderService
    {
        Task<CursorPage<RiderResponseDTO>> GetRidersAsync(string? cursor, int? pageSize);
        Task<RiderResponseDTO> GetRiderByUserIdAsync(string userId);
        Task<RiderResponseDTO> AddRiderAsync(RiderDTO riderDto);
        Task UpdateRiderAsync(string userId, RiderDTO riderDto);
        Task DeleteRiderAsync(string userId);
    }
}
