using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using RentalOperations.Model;

namespace RentalOperations.Data;

public sealed class MongoRentalIndexInitializer : IHostedService
{
    public const string ActiveRentalIndexName = "ux_rentals_one_active_per_motorcycle";
    public const string LegacyDuplicateQuarantineMessage =
        "Quarantined during active-rental index migration: duplicate open rental; review required.";

    private readonly MongoDbContext _context;

    public MongoRentalIndexInitializer(MongoDbContext context)
    {
        _context = context;
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

        await rentals.Indexes.CreateOneAsync(index, cancellationToken: cancellationToken);
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
