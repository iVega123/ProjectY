namespace RentalOperations.DTOs;

public sealed class MotorcycleRenameReservationDto
{
    public required string OldLicencePlate { get; init; }
    public required string NewLicencePlate { get; init; }
}
