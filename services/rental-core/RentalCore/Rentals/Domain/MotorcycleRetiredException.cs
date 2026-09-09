using RentalCore.Errors;

namespace RentalOperations.Domain;

public sealed class MotorcycleRetiredException(Guid motorcycleId)
    : BusinessRuleException(
        ProblemTypes.MotorcycleRetired,
        "Motorcycle retired",
        $"Motorcycle {motorcycleId} is retired and cannot be rented.");
