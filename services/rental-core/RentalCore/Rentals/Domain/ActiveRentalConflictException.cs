using RentalCore.Errors;

namespace RentalOperations.Domain;

public sealed class ActiveRentalConflictException : BusinessRuleException
{
    public ActiveRentalConflictException(Guid motorcycleId, Exception? innerException = null)
        : base(
            ProblemTypes.ActiveRental,
            "Active rental conflict",
            $"Motorcycle {motorcycleId} already has an active rental.")
    {
        Cause = innerException;
    }

    /// <summary>
    /// A causa vai para o log, e não para a resposta -- ela costuma ser a
    /// violação de índice que o banco levantou, com nome de constraint dentro.
    /// </summary>
    public Exception? Cause { get; }
}
