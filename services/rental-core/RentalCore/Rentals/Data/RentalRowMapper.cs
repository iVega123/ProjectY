using MongoDB.Bson;
using RentalOperations.Model;

namespace RentalOperations.Data;

/// <summary>Why a rental could not be expressed in the target schema.</summary>
public sealed record RentalMappingRefusal(string RentalId, string Reason);

/// <summary>
/// Maps the Mongo rental document onto the target schema's <c>rentals</c> row.
///
/// The identifier is derived rather than generated: the same document must land
/// on the same row on every pass, or the comparison that gates the cutover would
/// be comparing a store against a growing pile of duplicates. A Mongo ObjectId is
/// twelve bytes and a UUID is sixteen, so the remaining four are zero -- which is
/// reversible, and lets a row in the target schema still be traced to the document
/// it came from while both stores are live.
/// </summary>
public static class RentalRowMapper
{
    public static Guid ToRowId(ObjectId id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.ToByteArray().CopyTo(bytes);
        return new Guid(bytes);
    }

    /// <summary>
    /// The target schema accepts active, closed and cancelled. Quarantined has no
    /// place in it, and folding it into cancelled would erase the distinction the
    /// quarantine migrations exist to record -- "we set this aside for review" is
    /// not "this was cancelled". It is refused by name instead, so the rows that
    /// need a decision are counted rather than quietly rewritten.
    /// </summary>
    public static bool TryMapStatus(RentalStatus status, out string mapped)
    {
        mapped = status switch
        {
            RentalStatus.Active => "active",
            RentalStatus.Completed => "closed",
            RentalStatus.Cancelled => "cancelled",
            _ => string.Empty
        };
        return mapped.Length > 0;
    }

    public static RentalMappingRefusal? Refusal(Rental rental)
    {
        var id = rental._id?.ToString() ?? "(no id)";
        if (rental._id is null)
            return new RentalMappingRefusal(id, "document has no identifier");
        if (!Guid.TryParse(rental.MotorcycleId, out _))
            return new RentalMappingRefusal(id, $"motorcycle_id '{rental.MotorcycleId}' is not a UUID");
        if (!TryMapStatus(rental.Status, out _))
            return new RentalMappingRefusal(id, $"status '{rental.Status}' has no column value in the target schema");
        if (rental.PredictedEndDate <= rental.StartDate)
            return new RentalMappingRefusal(id, "predicted_ends_at is not after starts_at");
        return null;
    }
}
