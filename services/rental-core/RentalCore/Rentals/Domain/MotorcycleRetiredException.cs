namespace RentalOperations.Domain;

public sealed class MotorcycleRetiredException : InvalidOperationException
{
    public MotorcycleRetiredException(Guid motorcycleId)
        : base($"Motorcycle {motorcycleId} is retired and cannot be rented.")
    {
    }
}
