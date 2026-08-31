using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using RentalOperations.Model;

namespace RentalOperations.Data;

public sealed class MongoRentalIndexInitializer : IHostedService
{
    public const string ActiveRentalIndexName = "ux_rentals_one_active_per_motorcycle";

    private readonly MongoDbContext _context;

    public MongoRentalIndexInitializer(MongoDbContext context)
    {
        _context = context;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ClassifyLegacyRentalsAsync(cancellationToken);

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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
