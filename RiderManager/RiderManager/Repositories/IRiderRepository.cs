using RiderManager.Models;

namespace RiderManager.Repositories
{
    public interface IRiderRepository
    {
        Task<Rider> GetByIdAsync(string id);
        Task<Rider> GetByUserIdAsync(string userId);
        Task<List<Rider>> GetAllAsync();
        Task AddAsync(Rider rider);
        Task UpdateAsync(Rider rider);
        Task DeleteAsync(string id);
    }
}
