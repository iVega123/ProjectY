using RentalOperations.CrossCutting.Model;

namespace RentalOperations.CrossCutting.Services
{
    public interface IRiderManagerService
    {
        Task<Rider> GetRiderByIdAsync(string riderId);
    }
}
