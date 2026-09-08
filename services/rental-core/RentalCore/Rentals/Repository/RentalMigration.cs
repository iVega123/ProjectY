using System.Globalization;
using MongoDB.Driver;
using RentalOperations.Data;
using RentalOperations.Model;
using Npgsql;

namespace RentalOperations.Repository;

/// <summary>What the backfill moved, and what it could not.</summary>
public sealed record BackfillReport(int Total, int Applied, IReadOnlyList<string> Refused);

/// <summary>A field the two stores disagree about, for one rental.</summary>
public sealed record FieldDivergence(string RentalId, string Field, string Mongo, string Target);

/// <summary>
/// What the comparison found. The cutover is declared on this being clean, so it
/// reports absence and disagreement separately: a row that never arrived and a row
/// that arrived wrong fail for different reasons and are fixed in different places.
/// </summary>
public sealed record ComparisonReport(
    int MongoRows, int TargetRows, IReadOnlyList<string> Missing, IReadOnlyList<FieldDivergence> Divergent)
{
    public bool IsClean => Missing.Count == 0 && Divergent.Count == 0 && MongoRows == TargetRows;
}

/// <summary>
/// Moves the rentals Mongo already holds into the target schema, and then checks
/// that the two stores agree.
///
/// Three fields of the document have no column, and each is dropped for a reason
/// that holds on its own rather than because the table happens to lack it:
///
/// <list type="bullet">
/// <item><c>MotorcycleLicencePlate</c> — the target references motorcycle_id and
/// reaches the plate by join. A mutable business key is exactly what ADR 0004 took
/// out of the reference, so carrying it back would undo the change.</item>
/// <item><c>AdditionalCostsOrSavings</c> — decomposition, not information. Settlement
/// computes final_cost as init_cost plus this, so it is exactly
/// <c>final_cost - init_cost</c> and survives as arithmetic.</item>
/// <item><c>StatusMessage</c> — a sentence rendered from whether the return was early,
/// late or on time, which ends_at and predicted_ends_at already say. Settlement is
/// leaving for billing (#137) and the sentence belongs with it, not in the store.</item>
/// </list>
///
/// The <c>Quarantined</c> status is the fourth, and it is refused rather than dropped:
/// see <see cref="RentalRowMapper.TryMapStatus"/>. Quarantine is a migration-time
/// record — "this needs review" — and rewriting it as cancelled would assert a
/// decision nobody made. It stays a Mongo-side concept that ends with the migration,
/// and a row still carrying it blocks the cutover by name instead of vanishing.
/// </summary>
public static class RentalMigration
{
    public static async Task<BackfillReport> BackfillAsync(
        MongoDbContext db, RentalTargetWriter target, CancellationToken token)
    {
        var rentals = await db.Database.GetCollection<Rental>("Rentals")
            .Find(FilterDefinition<Rental>.Empty).ToListAsync(token);
        var refused = new List<string>();
        var applied = 0;
        foreach (var rental in rentals)
        {
            if (RentalRowMapper.Refusal(rental) is { } refusal)
            {
                refused.Add($"{refusal.RentalId}: {refusal.Reason}");
                continue;
            }
            if (await target.MirrorAsync(rental, token)) applied++;
            else refused.Add($"{rental._id}: target store rejected the row");
        }
        return new BackfillReport(rentals.Count, applied, refused);
    }

    public static async Task<ComparisonReport> CompareAsync(
        MongoDbContext db, NpgsqlDataSource target, CancellationToken token)
    {
        var rentals = await db.Database.GetCollection<Rental>("Rentals")
            .Find(FilterDefinition<Rental>.Empty).ToListAsync(token);

        var rows = new Dictionary<Guid, (string Rider, Guid Motorcycle, DateTime? Ends, decimal Init, decimal? Final, string Status)>();
        await using (var connection = await target.OpenConnectionAsync(token))
        {
            await using var command = new NpgsqlCommand(
                "SELECT id, rider_id, motorcycle_id, ends_at, init_cost, final_cost, status FROM rentals", connection);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rows[reader.GetGuid(0)] = (reader.GetString(1), reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetDateTime(3), reader.GetDecimal(4),
                    reader.IsDBNull(5) ? null : reader.GetDecimal(5), reader.GetString(6));
            }
        }

        var missing = new List<string>();
        var divergent = new List<FieldDivergence>();
        foreach (var rental in rentals)
        {
            if (rental._id is null) continue;
            var id = RentalRowMapper.ToRowId(rental._id.Value);
            if (!rows.TryGetValue(id, out var row))
            {
                missing.Add(rental._id.Value.ToString());
                continue;
            }
            var name = rental._id.Value.ToString();
            void Check(string field, string mongo, string stored)
            {
                if (!string.Equals(mongo, stored, StringComparison.Ordinal))
                    divergent.Add(new FieldDivergence(name, field, mongo, stored));
            }
            Check("rider_id", rental.UserId, row.Rider);
            Check("motorcycle_id", rental.MotorcycleId, row.Motorcycle.ToString());
            Check("init_cost", rental.InitCost.ToString("0.00", CultureInfo.InvariantCulture), row.Init.ToString("0.00", CultureInfo.InvariantCulture));
            Check("ends_at", Moment(rental.EndDate), Moment(row.Ends));
            Check("final_cost", rental.FinalCost == 0m ? "-" : rental.FinalCost.ToString("0.00", CultureInfo.InvariantCulture),
                row.Final is { } stored ? stored.ToString("0.00", CultureInfo.InvariantCulture) : "-");
            RentalRowMapper.TryMapStatus(rental.Status, out var expected);
            Check("status", expected.Length == 0 ? rental.Status.ToString() : expected, row.Status);
        }
        return new ComparisonReport(rentals.Count, rows.Count, missing, divergent);
    }

    // Every value is rendered under the invariant culture: a divergence report that
    // reads differently on a machine with a different locale is not a report, and
    // the same number would print two ways depending on who ran the comparison.
    // Compared to the second: Mongo keeps milliseconds the column does not, and a
    // difference that fine is a formatting artefact, not a divergence worth blocking on.
    private static string Moment(DateTime? value) => value is { } moment
        ? DateTime.SpecifyKind(moment, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        : "-";
}
