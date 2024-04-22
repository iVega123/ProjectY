using RentalOperations.CrossCutting.Model;

namespace RentalOperations.CrossCutting.Services
{
    public interface IMotorcycleService
    {
        Task<Motorcycle> GetMotorcycleByIdAsync(string motorcycleId);
    }
}
