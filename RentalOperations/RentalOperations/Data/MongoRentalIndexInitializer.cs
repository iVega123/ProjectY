using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using RentalOperations.CrossCutting.Services;
using RentalOperations.Model;

namespace RentalOperations.Data;

public sealed class MongoRentalIndexInitializer : IHostedService
{
    public const string ActiveRentalIndexName = "ux_rentals_one_active_per_motorcycle";
    public const string UserRentalPageIndexName = "ix_rentals_user_cursor";
    public const string MotorcycleAvailabilityIndexName = "ix_rentals_motorcycle_availability";
    public const string LegacyDuplicateQuarantineMessage =
        "Quarantined during active-rental index migration: duplicate open rental; review required.";
    public const string RetiredMotorcycleQuarantineMessage =
        "Quarantined during motorcycle-reference migration: motorcycle was already retired.";

    private readonly MongoDbContext _context;
    private readonly IServiceScopeFactory _scopeFactory;

    public MongoRentalIndexInitializer(MongoDbContext context, IServiceScopeFactory scopeFactory)
    {
        _context = context;
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ClassifyLegacyRentalsAsync(cancellationToken);
        await QuarantineDuplicateActiveRentalsAsync(cancellationToken);

        var rentals = _context.Database.GetCollection<Rental>("Rentals");
        var index = new CreateIndexModel<Rental>(
            Builders<Rental>.IndexKeys.Ascending(rental => rental.MotorcycleLicencePlate),
            new CreateIndexOptions<Rental>
            {
                Name = ActiveRentalIndexName,
                Unique = true,
                PartialFilterExpression = Builders<Rental>.Filter.Eq(
                    rental => rental.Status,
                    RentalStatus.Active)
            });

        var userPageIndex = new CreateIndexModel<Rental>(
            Builders<Rental>.IndexKeys
                .Ascending(rental => rental.UserId)
                .Ascending(rental => rental._id),
            new CreateIndexOptions { Name = UserRentalPageIndexName });
        var availabilityIndex = new CreateIndexModel<Rental>(
            Builders<Rental>.IndexKeys
                .Ascending(rental => rental.MotorcycleLicencePlate)
                .Ascending(rental => rental.Status)
                .Ascending(rental => rental.StartDate)
                .Ascending(rental => rental.PredictedEndDate),
            new CreateIndexOptions<Rental>
            {
                Name = MotorcycleAvailabilityIndexName,
                PartialFilterExpression = Builders<Rental>.Filter.Eq(
                    rental => rental.Status,
                    RentalStatus.Active)
            });

        await rentals.Indexes.CreateManyAsync(
            [index, userPageIndex, availabilityIndex],
            cancellationToken);
        await ReconcileMotorcycleClaimsAsync(cancellationToken);
        await EnsureHistoricalMotorcycleReferencesAsync(cancellationToken);
    }

    private async Task ClassifyLegacyRentalsAsync(CancellationToken cancellationToken)
    {
        var rentals = _context.Database.GetCollection<BsonDocument>("Rentals");
        var missingStatus = Builders<BsonDocument>.Filter.Exists("status", false);
        var openRental = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Exists("endDate", false),
            Builders<BsonDocument>.Filter.Eq("endDate", BsonNull.Value),
            Builders<BsonDocument>.Filter.Eq("endDate", DateTime.MinValue));

        await rentals.UpdateManyAsync(
            Builders<BsonDocument>.Filter.And(missingStatus, openRental),
            Builders<BsonDocument>.Update
                .Set("status", RentalStatus.Active.ToString())
                .Unset("endDate"),
            cancellationToken: cancellationToken);

        await rentals.UpdateManyAsync(
            Builders<BsonDocument>.Filter.And(
                missingStatus,
                Builders<BsonDocument>.Filter.Gt("endDate", DateTime.MinValue)),
            Builders<BsonDocument>.Update.Set("status", RentalStatus.Completed.ToString()),
            cancellationToken: cancellationToken);
    }

    private async Task QuarantineDuplicateActiveRentalsAsync(CancellationToken cancellationToken)
    {
        var rentals = _context.Database.GetCollection<BsonDocument>("Rentals");
        var duplicateGroups = await rentals.Aggregate()
            .Match(new BsonDocument("status", RentalStatus.Active.ToString()))
            .Sort(new BsonDocument
            {
                ["MotorcycleLicencePlate"] = 1,
                ["startDate"] = 1,
                ["_id"] = 1
            })
            .Group(new BsonDocument
            {
                ["_id"] = "$MotorcycleLicencePlate",
                ["rentalIds"] = new BsonDocument("$push", "$_id"),
                ["count"] = new BsonDocument("$sum", 1)
            })
            .Match(new BsonDocument("count", new BsonDocument("$gt", 1)))
            .ToListAsync(cancellationToken);

        foreach (var group in duplicateGroups)
        {
            var duplicateIds = group["rentalIds"].AsBsonArray.Skip(1);
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.In("_id", duplicateIds),
                Builders<BsonDocument>.Filter.Eq("status", RentalStatus.Active.ToString()));
            var update = Builders<BsonDocument>.Update
                .Set("status", RentalStatus.Quarantined.ToString())
                .Set("statusMessage", LegacyDuplicateQuarantineMessage);

            await rentals.UpdateManyAsync(filter, update, cancellationToken: cancellationToken);
        }
    }

    private async Task ReconcileMotorcycleClaimsAsync(CancellationToken cancellationToken)
    {
        var rentals = _context.Database.GetCollection<Rental>("Rentals");
        var claims = _context.Database.GetCollection<MotorcycleClaim>("MotorcycleClaims");
        await RemoveStaleRentalClaimsAsync(rentals, claims, cancellationToken);
        var activeRentals = await rentals
            .Find(rental => rental.Status == RentalStatus.Active)
            .ToListAsync(cancellationToken);

        foreach (var rental in activeRentals)
        {
            var rentalId = rental._id!.Value.ToString();
            try
            {
                await claims.InsertOneAsync(new MotorcycleClaim
                {
                    MotorcycleLicencePlate = rental.MotorcycleLicencePlate,
                    Kind = MotorcycleClaimKind.ActiveRental,
                    RentalId = rentalId,
                    CreatedAtUtc = DateTime.UtcNow
                }, cancellationToken: cancellationToken);
            }
            catch (MongoWriteException exception)
                when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                var existing = await claims
                    .Find(claim => claim.MotorcycleLicencePlate == rental.MotorcycleLicencePlate)
                    .FirstOrDefaultAsync(cancellationToken);
                if (existing?.Kind == MotorcycleClaimKind.Retired)
                {
                    await rentals.UpdateOneAsync(
                        candidate => candidate._id == rental._id &&
                                     candidate.Status == RentalStatus.Active,
                        Builders<Rental>.Update
                            .Set(candidate => candidate.Status, RentalStatus.Quarantined)
                            .Set(candidate => candidate.StatusMessage, RetiredMotorcycleQuarantineMessage),
                        cancellationToken: cancellationToken);
                }
                else if (existing?.Kind == MotorcycleClaimKind.ActiveRental &&
                         existing.RentalId != rentalId)
                {
                    var claimedRentalIsActive = ObjectId.TryParse(existing.RentalId, out var claimedRentalId) &&
                        await rentals.Find(candidate =>
                                candidate._id == claimedRentalId &&
                                candidate.Status == RentalStatus.Active)
                            .AnyAsync(cancellationToken);
                    if (!claimedRentalIsActive)
                    {
                        await claims.ReplaceOneAsync(
                            claim => claim.MotorcycleLicencePlate == rental.MotorcycleLicencePlate &&
                                     claim.Kind == MotorcycleClaimKind.ActiveRental &&
                                     claim.RentalId == existing.RentalId,
                            new MotorcycleClaim
                            {
                                MotorcycleLicencePlate = rental.MotorcycleLicencePlate,
                                Kind = MotorcycleClaimKind.ActiveRental,
                                RentalId = rentalId,
                                CreatedAtUtc = DateTime.UtcNow
                            },
                            cancellationToken: cancellationToken);
                    }
                }
            }
        }
    }

    private static async Task RemoveStaleRentalClaimsAsync(
        IMongoCollection<Rental> rentals,
        IMongoCollection<MotorcycleClaim> claims,
        CancellationToken cancellationToken)
    {
        var staleBeforeUtc = DateTime.UtcNow.AddMinutes(-5);
        var staleCandidates = await claims.Find(claim =>
                claim.Kind == MotorcycleClaimKind.ActiveRental &&
                claim.CreatedAtUtc < staleBeforeUtc)
            .ToListAsync(cancellationToken);

        foreach (var claim in staleCandidates)
        {
            var hasActiveRental = ObjectId.TryParse(claim.RentalId, out var rentalId) &&
                await rentals.Find(rental =>
                        rental._id == rentalId &&
                        rental.Status == RentalStatus.Active)
                    .AnyAsync(cancellationToken);
            if (!hasActiveRental)
            {
                await claims.DeleteOneAsync(candidate =>
                    candidate.MotorcycleLicencePlate == claim.MotorcycleLicencePlate &&
                    candidate.Kind == MotorcycleClaimKind.ActiveRental &&
                    candidate.RentalId == claim.RentalId &&
                    candidate.CreatedAtUtc == claim.CreatedAtUtc,
                    cancellationToken);
            }
        }
    }

    private async Task EnsureHistoricalMotorcycleReferencesAsync(CancellationToken cancellationToken)
    {
        var rentals = _context.Database.GetCollection<Rental>("Rentals");
        var licensePlates = await rentals
            .Distinct<string>("MotorcycleLicencePlate", FilterDefinition<Rental>.Empty)
            .ToListAsync(cancellationToken);
        if (licensePlates.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var motorcycleService = scope.ServiceProvider.GetRequiredService<IMotorcycleService>();
        await motorcycleService.EnsureHistoricalReferencesAsync(licensePlates);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
