using MotoHub.DTOs;
using MotoHub.Entities;

namespace MotoHub.Services
{
    public interface IMotorcycleService
    {
        IEnumerable<MotorcycleDTO> GetAllMotorcycles();
        Task<MotorcycleDTO?> GetMotorcycleByLicensePlateAsync(string licensePlate);
        void CreateMotorcycle(MotorcycleDTO motorcycleDto);
        Task UpdateMotorcycleAsync(string licensePlate, string newLicencePlate);
        Task<OperationResult> DeleteMotorcycle(string licensePlate);
        Task EnsureHistoricalReferencesAsync(IEnumerable<string> licensePlates);
        bool LicensePlateExists(string licensePlate);
    }
}
