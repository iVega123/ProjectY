using MotoHub.Models;

using ProjectY.Shared.Pagination;

namespace MotoHub.Repositories
{
    public interface IMotorcycleRepository
    {
        Task<CursorPage<Motorcycle>> GetPageAsync(string? cursor, int? pageSize);
        Motorcycle? GetById(Guid id);
        Task<Motorcycle?> GetByIdAsync(Guid id);
        void Add(Motorcycle motorcycle);
        void Update(Motorcycle motorcycle);
        bool LicensePlateExists(string licensePlate);
        Task<Motorcycle?> GetByLicensePlateAsync(string licensePlate);
    }
}
