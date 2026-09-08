using MongoDB.Bson;
using MongoDB.Driver;
using RentalOperations.Data;
using RentalOperations.Model;

namespace RentalOperations.Repository;

/// <summary>
/// Re-mirrors rentals that never reached the target schema.
///
/// The failure this exists for is routine, not exotic: motorcycles arrive in the
/// target through a reconciling projector, so a motorcycle registered moments ago
/// is not there yet, and a rental referencing it is refused by the foreign key.
/// Without a retry that rental stays absent until something happens to update it,
/// which turns a projection delay into permanent divergence -- and divergence is
/// what decides whether the cutover can be declared at all.
///
/// It looks only at the recent past. The full backfill is a separate step with its
/// own verification; this is the live safety net under the dual write, and a net
/// that tried to be the backfill would quietly become an unverified one.
/// </summary>
public sealed class RentalMirrorReconciler(
    MongoDbContext db,
    RentalTargetWriter target,
    ILogger<RentalMirrorReconciler> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(Interval, token);
            try
            {
                var missing = await ReconcileAsync(db, target, DateTime.UtcNow - Window, token);
                if (missing > 0)
                    log.LogInformation("Re-mirrored {Count} rentals that had not reached the target schema", missing);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                log.LogWarning(error, "Rental reconciliation delayed; retrying on the next pass");
            }
        }
    }

    public static async Task<int> ReconcileAsync(
        MongoDbContext db, RentalTargetWriter target, DateTime since, CancellationToken token)
    {
        // The ObjectId carries its own creation time, so the recent window costs no
        // extra field and no extra index.
        var recent = await db.Database.GetCollection<Rental>("Rentals")
            .Find(Builders<Rental>.Filter.Gt(rental => rental._id, ObjectId.GenerateNewId(since)))
            .ToListAsync(token);
        if (recent.Count == 0) return 0;

        var present = await target.PresentAsync(
            recent.Where(rental => rental._id is not null)
                  .Select(rental => RentalRowMapper.ToRowId(rental._id!.Value))
                  .ToList(),
            token);

        var repaired = 0;
        foreach (var rental in recent)
        {
            if (rental._id is null || present.Contains(RentalRowMapper.ToRowId(rental._id.Value))) continue;
            if (await target.MirrorAsync(rental, token))
            {
                target.CountReconciled();
                repaired++;
            }
        }
        return repaired;
    }
}
