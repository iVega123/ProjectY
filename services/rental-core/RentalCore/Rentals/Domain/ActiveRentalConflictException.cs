using RentalCore.Errors;

namespace RentalOperations.Domain;

/// <summary>
/// A violação do índice único vai como InnerException, e não como propriedade
/// própria.
///
/// A cadeia de inner exceptions é o que o ILogger serializa e o que
/// Exception.ToString() percorre; guardar a causa noutro lugar tira o SQLSTATE
/// e o nome da constraint do log -- exatamente o que se procura para entender
/// uma corrida perdida. Ela continua fora da RESPOSTA: quem decide isso é o
/// ProblemDetailsExceptionHandler, que só publica o Detail.
/// </summary>
public sealed class ActiveRentalConflictException(Guid motorcycleId, Exception? innerException = null)
    : BusinessRuleException(
        ProblemTypes.ActiveRental,
        "Active rental conflict",
        $"Motorcycle {motorcycleId} already has an active rental.",
        innerException);
