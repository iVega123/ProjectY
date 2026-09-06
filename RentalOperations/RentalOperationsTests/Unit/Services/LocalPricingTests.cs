using RentalOperations.Services;

namespace RentalOperationsTests.Unit.Services;

public sealed class LocalPricingTests
{
    [Fact]
    public void UnknownRiderUsesBaseAndKnownScoreSurvivesOlderDelivery()
    {
        var rider = Guid.NewGuid().ToString();
        var baseline = LocalPricing.DailyRate(7);
        Assert.Equal(baseline, LocalPricing.DailyRate(7, rider));
        LocalPricing.ApplyScore(rider, 20, 200);
        var discounted = decimal.Round(baseline * 0.95m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(discounted, LocalPricing.DailyRate(7, rider));
        LocalPricing.ApplyScore(rider, 90, 100);
        Assert.Equal(discounted, LocalPricing.DailyRate(7, rider));
        Assert.Throws<InvalidDataException>(() => LocalPricing.ApplyScore(rider, -1, 300));
    }
}
