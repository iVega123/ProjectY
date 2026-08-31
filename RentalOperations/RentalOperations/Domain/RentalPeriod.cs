using RentalOperations.Model;

namespace RentalOperations.Domain;

public static class RentalPeriod
{
    public static bool Overlaps(
        Rental rental,
        DateTime requestedStart,
        DateTime requestedEnd)
    {
        if (rental.Status == RentalStatus.Cancelled)
        {
            return false;
        }

        var existingEnd = rental.EndDate ?? rental.PredictedEndDate;
        return requestedStart < existingEnd && requestedEnd > rental.StartDate;
    }
}
