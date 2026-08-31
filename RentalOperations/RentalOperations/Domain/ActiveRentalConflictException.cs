namespace RentalOperations.Domain;

public sealed class ActiveRentalConflictException : Exception
{
    public ActiveRentalConflictException(string licencePlate, Exception? innerException = null)
        : base($"Motorcycle {licencePlate} already has an active rental.", innerException)
    {
    }
}
