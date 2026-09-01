using MotoHub.Models;

namespace MotoHub.Repositories
{
    public interface IMotorcycleRepository
    {
        IEnumerable<Motorcycle> GetAll();
        Motorcycle? GetById(string id);
        void Add(Motorcycle motorcycle);
        void Update(Motorcycle motorcycle);
        Task<bool> RetireAsync(string id, DateTime retiredAtUtc, string reason);
        Task EnsureHistoricalReferenceAsync(string licensePlate, DateTime retiredAtUtc);
        bool LicensePlateExists(string licensePlate);
        Task<Motorcycle?> GetByLicensePlateAsync(string licensePlate);
    }
}
