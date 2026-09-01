using MotoHub.DTOs;
using MotoHub.Entities;

using ProjectY.Shared.Pagination;

namespace MotoHub.Services
{
    public interface IMotorcycleService
    {
        Task<CursorPage<MotorcycleDTO>> GetMotorcyclesAsync(string? cursor, int? pageSize);
        Task<MotorcycleDTO?> GetMotorcycleByLicensePlateAsync(string licensePlate);
        void CreateMotorcycle(MotorcycleDTO motorcycleDto);
        Task UpdateMotorcycleAsync(string licensePlate, string newLicencePlate);
        Task<OperationResult> DeleteMotorcycle(string licensePlate);
        Task EnsureHistoricalReferencesAsync(IEnumerable<string> licensePlates);
        bool LicensePlateExists(string licensePlate);
    }
}
