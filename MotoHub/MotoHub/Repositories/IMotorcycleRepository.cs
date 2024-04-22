using MotoHub.Models;

namespace MotoHub.Repositories
{
    public interface IMotorcycleRepository
    {
        IEnumerable<Motorcycle> GetAll();
        Motorcycle? GetById(string id);
        void Add(Motorcycle motorcycle);
        void Update(Motorcycle motorcycle);
        void Delete(string id);
        bool LicensePlateExists(string licensePlate);
        Task<Motorcycle?> GetByLicensePlateAsync(string licensePlate);
    }
}
