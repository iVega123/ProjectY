namespace RentalOperations.Domain;

public sealed class MotorcycleRetiredException : InvalidOperationException
{
    public MotorcycleRetiredException(string licencePlate)
        : base($"Motorcycle {licencePlate} is retired and cannot be rented.")
    {
    }
}
