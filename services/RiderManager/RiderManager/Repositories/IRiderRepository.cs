using RiderManager.Models;

using ProjectY.Shared.Pagination;

namespace RiderManager.Repositories
{
    public interface IRiderRepository
    {
        Task<Rider> GetByIdAsync(string id);
        Task<Rider> GetByUserIdAsync(string userId);
        Task<CursorPage<Rider>> GetPageAsync(string? cursor, int? pageSize);
        Task AddAsync(Rider rider);
        Task UpdateAsync(Rider rider);
        Task DeleteAsync(string id);
    }
}
