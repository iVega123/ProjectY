namespace RentalOperations.Domain;

public sealed class ActiveRentalConflictException : Exception
{
    public ActiveRentalConflictException(Guid motorcycleId, Exception? innerException = null)
        : base($"Motorcycle {motorcycleId} already has an active rental.", innerException)
    {
    }
}
