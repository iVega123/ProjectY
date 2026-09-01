using RiderManager.DTOs;

using ProjectY.Shared.Pagination;

namespace RiderManager.Managers
{
    public interface IRiderManager
    {
        Task AddRiderAsync(RiderDTO riderDto);
        Task UpdateRiderAsync(string userId, RiderDTO riderDto);
        Task DeleteRiderAsync(string userId);
        Task UpdateRiderImageAsync(string userId, IFormFile cnhFile, string? objectName = null);
        Task<CursorPage<RiderResponseDTO>> GetRidersAsync(string? cursor, int? pageSize);
        Task<RiderResponseDTO> GetRiderByUserIdAsync(string userId);
    }
}
