using MotoHub.DTOs;
using MotoHub.Entities;

using ProjectY.Shared.Pagination;

namespace MotoHub.Services
{
    public interface IMotorcycleService
    {
        Task<CursorPage<MotorcycleDTO>> GetMotorcyclesAsync(string? cursor, int? pageSize);
        Task<MotorcycleDTO?> GetMotorcycleByLicensePlateAsync(string licensePlate);
        Task<MotorcycleDTO?> GetMotorcycleByIdAsync(Guid id);
        Task<IReadOnlyList<MotorcycleDTO>> GetMotorcyclesByIdsAsync(IReadOnlyCollection<Guid> ids);
        void CreateMotorcycle(MotorcycleDTO motorcycleDto);
        Task UpdateMotorcycleAsync(string licensePlate, string newLicencePlate);
        Task<OperationResult> DeleteMotorcycle(string licensePlate);
        bool LicensePlateExists(string licensePlate);
    }
}
