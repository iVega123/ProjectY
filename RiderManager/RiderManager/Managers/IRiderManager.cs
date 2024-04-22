using RiderManager.DTOs;

namespace RiderManager.Managers
{
    public interface IRiderManager
    {
        Task AddRiderAsync(RiderDTO riderDto);
        Task UpdateRiderAsync(string userId, RiderDTO riderDto);
        Task DeleteRiderAsync(string userId);
        Task UpdateRiderImageAsync(string userId, IFormFile cnhFile);
        Task<IEnumerable<RiderResponseDTO>> GetAllRidersAsync();
        Task<RiderResponseDTO> GetRiderByUserIdAsync(string userId);
    }
}
