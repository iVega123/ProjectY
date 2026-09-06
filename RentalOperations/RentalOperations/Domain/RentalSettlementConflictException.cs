namespace RentalOperations.Domain;

public sealed class RentalSettlementConflictException()
    : Exception("The rental changed before settlement could be saved. Reload the rental to obtain its stored settlement.");
