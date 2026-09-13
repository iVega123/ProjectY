using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using RentalOperations.Services;

namespace RentalOperationsTests.Unit.Services;

/// <summary>
/// The degradation row for risk-pricing: demand-based pricing stops, the rental
/// is still priced. Nothing here reaches risk-pricing -- that is the point. If
/// pricing ever required a score, the unscored rider below would throw instead
/// of paying the base rate, and the counter would stay at zero.
/// </summary>
public sealed class PricingFallbackTests
{
    [Fact]
    [Trait("Degradation", "risk-pricing")]
    public void WithoutAScore_TheRiderPaysTheBaseRate_AndTheFallbackIsCounted()
    {
        using var activity = new Activity("pricing-fallback-test").Start();
        using var recorded = new FallbackPaths(activity);

        var price = LocalPricing.DailyRate(7, "never-scored-" + Guid.NewGuid());

        Assert.Equal(LocalPricing.DailyRate(7), price);
        Assert.True(price > 0);
        Assert.Contains("unscored-base-rate", recorded.Paths);
        Assert.Equal("risk-pricing", activity.GetTagItem("projecty.degradation"));
    }

    [Fact]
    [Trait("Degradation", "risk-pricing")]
    public void AScoredRider_IsPricedWithoutCountingTheScoreFallback()
    {
        var rider = "scored-" + Guid.NewGuid();
        LocalPricing.ApplyScore(rider, 20, 100);
        using var activity = new Activity("pricing-scored-test").Start();
        using var recorded = new FallbackPaths(activity);

        var price = LocalPricing.DailyRate(7, rider);

        Assert.Equal(decimal.Round(LocalPricing.DailyRate(7) * 0.95m, 2, MidpointRounding.AwayFromZero), price);
        Assert.DoesNotContain("unscored-base-rate", recorded.Paths);
    }

    /// <summary>
    /// Measurements recorded under this test's own activity only. Other test
    /// classes price riders in parallel, and the instrument is process-wide.
    /// </summary>
    private sealed class FallbackPaths : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentBag<string> paths = new();

        public FallbackPaths(Activity owner)
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == Degradation.MeterName && instrument.Name == Degradation.InstrumentName)
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                if (Activity.Current != owner) return;
                foreach (var tag in tags)
                    if (tag.Key == "path" && tag.Value is string path) paths.Add(path);
            });
            listener.Start();
        }

        public IReadOnlyCollection<string> Paths => paths;

        public void Dispose() => listener.Dispose();
    }
}
