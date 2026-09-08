using RentalOperations.Domain;
using RentalOperations.Model;

namespace RentalOperationsTests.Unit.Domain;

public sealed class RentalPeriodTests
{
    private static readonly DateTime ExistingStart = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ExistingEnd = new(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Overlaps_WhenRequestedPeriodIntersectsActiveRental_ReturnsTrue()
    {
        var rental = CreateRental(RentalStatus.Active);

        var overlaps = RentalPeriod.Overlaps(
            rental,
            ExistingStart.AddDays(1),
            ExistingEnd.AddDays(1));

        Assert.True(overlaps);
    }

    [Fact]
    public void Overlaps_WhenRequestedRentalStartsAtPreviousEnd_ReturnsFalse()
    {
        var rental = CreateRental(RentalStatus.Completed, ExistingEnd);

        var overlaps = RentalPeriod.Overlaps(
            rental,
            ExistingEnd,
            ExistingEnd.AddDays(7));

        Assert.False(overlaps);
    }

    [Fact]
    public void Overlaps_WhenRequestedRentalEndsAtNextStart_ReturnsFalse()
    {
        var rental = CreateRental(RentalStatus.Active);

        var overlaps = RentalPeriod.Overlaps(
            rental,
            ExistingStart.AddDays(-7),
            ExistingStart);

        Assert.False(overlaps);
    }

    [Theory]
    [InlineData(RentalStatus.Cancelled)]
    [InlineData(RentalStatus.Quarantined)]
    public void Overlaps_WhenExistingRentalIsUnavailableForScheduling_ReturnsFalse(RentalStatus status)
    {
        var rental = CreateRental(status);

        var overlaps = RentalPeriod.Overlaps(rental, ExistingStart, ExistingEnd);

        Assert.False(overlaps);
    }

    private static Rental CreateRental(RentalStatus status, DateTime? endDate = null) => new()
    {
        MotorcycleLicencePlate = "TEST-0001",
        UserId = "rider-1",
        StartDate = ExistingStart,
        PredictedEndDate = ExistingEnd,
        EndDate = endDate,
        Status = status
    };
}
