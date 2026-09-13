using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RentalOperations.Services;

/// <summary>
/// A fallback that was taken, counted where it happens.
///
/// A refusal and a degradation are different facts: <see cref="DependencyFailure"/>
/// counts requests the service turned away, this counts work that continued
/// without a dependency. Each fallback path records its own label, so a rising
/// series names the dependency instead of reading as generic noise. Exported by
/// OTel as <c>dependency.degradations</c>, which Prometheus names
/// <c>dependency_degradations_total</c>.
/// </summary>
public static class Degradation
{
    public const string MeterName = "ProjectY.Resilience";
    public const string InstrumentName = "dependency.degradations";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Degradations = Meter.CreateCounter<long>(InstrumentName);

    public static void Record(string dependency, string path)
    {
        Degradations.Add(1,
            new KeyValuePair<string, object?>("dependency", dependency),
            new KeyValuePair<string, object?>("path", path));
        Activity.Current?.SetTag("projecty.degradation", dependency);
    }
}
