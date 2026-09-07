namespace MotoHub.CrossCutting
{
    public interface IRentalOperationService
    {
        Task<bool> GetRentalsByMotorcycleLicencePlateAsync(string licensePlate);
        Task<bool> TryRetireMotorcycleAsync(string licensePlate);
        Task<bool> TryReserveMotorcycleRenameAsync(string oldLicensePlate, string newLicensePlate);
    }
}
