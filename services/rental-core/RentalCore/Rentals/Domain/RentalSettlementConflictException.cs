using RentalCore.Errors;

namespace RentalOperations.Domain;

public sealed class RentalSettlementConflictException()
    : BusinessRuleException(
        ProblemTypes.SettlementConflict,
        "Rental settlement conflict",
        "The rental changed before settlement could be saved. Reload the rental to obtain its stored settlement.");
