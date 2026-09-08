namespace MotoHub.DTOs;

public sealed class HistoricalMotorcycleReferencesRequest
{
    public required IReadOnlyCollection<string> LicensePlates { get; init; }
}
