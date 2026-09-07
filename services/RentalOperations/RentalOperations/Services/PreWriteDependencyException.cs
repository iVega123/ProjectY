namespace RentalOperations.Services;

// Only read-only preflight operations may use this signal; writes can have ambiguous outcomes.
public sealed class PreWriteDependencyException(Exception innerException)
    : Exception("Dependency failed before any side effect.", innerException);
