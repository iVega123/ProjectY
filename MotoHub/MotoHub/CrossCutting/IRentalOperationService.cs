namespace MotoHub.CrossCutting
{
    public interface IRentalOperationService
    {
        Task<bool> GetRentalsByMotorcycleLicencePlateAsync(string licensePlate);
    }
}
