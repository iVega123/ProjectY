using RiderManager.DTOs;
using RiderManager.Models;

namespace RiderManager.Services.RiderServices
{
    public interface IRiderService
    {
        Task<IEnumerable<RiderResponseDTO>> GetAllRidersAsync();
        Task<RiderResponseDTO> GetRiderByUserIdAsync(string userId);
        Task<RiderResponseDTO> AddRiderAsync(RiderDTO riderDto);
        Task UpdateRiderAsync(string userId, RiderDTO riderDto);
        Task DeleteRiderAsync(string userId);
    }
}
