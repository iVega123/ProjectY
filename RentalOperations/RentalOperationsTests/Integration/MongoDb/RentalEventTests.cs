using MongoDB.Driver;
using MongoDB.Bson;
using ProjectY.Events;
using RentalOperations.Data;
using RentalOperations.Model;
using RentalOperations.Repository;
using RentalOperations.Services;
using Testcontainers.MongoDb;

namespace RentalOperationsTests.Integration.MongoDb;

public sealed class RentalEventTests : IAsyncLifetime
{
    private readonly MongoDbContainer database = new MongoDbBuilder("mongo:8.0").Build();
    public Task InitializeAsync() => database.StartAsync();
    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [Fact]
    public async Task LegacyBackfillIsStableAcrossReplicasAcknowledgementAndConcurrentClose()
    {
        var context = new MongoDbContext(database.GetConnectionString(), "legacy_events");
        var rentals = context.Database.GetCollection<Rental>("Rentals");
        var raw = context.Database.GetCollection<BsonDocument>("Rentals");
        var rental = new Rental
        {
            MotorcycleLicencePlate = "OLD1234", UserId = "legacy-rider",
            StartDate = DateTime.UtcNow.AddDays(-2), PredictedEndDate = DateTime.UtcNow.AddDays(5)
        };
        var document = rental.ToBsonDocument();
        document.Remove("PendingEvents");
        document.Remove("MotorcycleId");
        await raw.InsertOneAsync(document);
        var repository = new RentalRepository(context);
        var stale = await repository.GetRentalByIdAsync(rental._id!.Value.ToString());
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RentalKafkaRelay.BackfillActiveRentalsAsync(rentals)));
        var saved = await repository.GetRentalByIdAsync(rental._id.Value.ToString());
        var start = Assert.Single(saved.PendingEvents);
        var payload = RentalEvent.Parser.ParseFrom(start.Payload);
        Assert.Equal($"{rental._id}:rental.started:v1", payload.EventId);
        Assert.Equal("legacy-rider", payload.RiderId);
        Assert.Equal($"legacy-rental:{rental._id}", payload.MotorcycleId);
        Assert.Equal(new DateTimeOffset(rental._id.Value.CreationTime).ToUnixTimeMilliseconds(), payload.OccurredAtMs);
        stale.Status = RentalStatus.Completed;
        stale.EndDate = DateTime.UtcNow;
        await repository.UpdateRentalAsync(stale);
        saved = await repository.GetRentalByIdAsync(rental._id.Value.ToString());
        Assert.Equal(2, saved.PendingEvents.Count);
        await rentals.UpdateOneAsync(r => r._id == rental._id,
            Builders<Rental>.Update.PullFilter(r => r.PendingEvents, e => e.Id == start.Id));
        await repository.UpdateRentalAsync(stale);
        await RentalKafkaRelay.BackfillActiveRentalsAsync(rentals);
        saved = await repository.GetRentalByIdAsync(rental._id.Value.ToString());
        Assert.Equal("rental.closed", Assert.Single(saved.PendingEvents).Topic);

        // An acknowledged active rental is not a legacy document.
        var active = new Rental { MotorcycleLicencePlate = "NEW1234", UserId = "new-rider" };
        await rentals.InsertOneAsync(active);
        await RentalKafkaRelay.BackfillActiveRentalsAsync(rentals);
        Assert.Empty((await repository.GetRentalByIdAsync(active._id!.Value.ToString())).PendingEvents);
        document["_id"] = ObjectId.GenerateNewId();
        document["status"] = "Completed";
        await raw.InsertOneAsync(document);
        await RentalKafkaRelay.BackfillActiveRentalsAsync(rentals);
        Assert.False((await raw.Find(new BsonDocument("_id", document["_id"])).SingleAsync()).Contains("PendingEvents"));
    }

    [Fact]
    public async Task RentalAndPendingEventsAreOneDocument_AndAcknowledgingStartPreservesClose()
    {
        var context = new MongoDbContext(database.GetConnectionString(), "rental_events");
        var repository = new RentalRepository(context);
        var rental = new Rental
        {
            MotorcycleId = "immutable-moto-1",
            MotorcycleLicencePlate = "ABC1D23",
            UserId = "rider-1",
            StartDate = DateTime.UtcNow,
            PredictedEndDate = DateTime.UtcNow.AddDays(7)
        };
        await repository.CreateRentalAsync(rental);
        var saved = await repository.GetRentalByIdAsync(rental._id!.Value.ToString());
        var start = Assert.Single(saved.PendingEvents);
        var payload = RentalEvent.Parser.ParseFrom(start.Payload);
        Assert.Equal("immutable-moto-1", payload.MotorcycleId);
        Assert.Equal("rider-1", payload.RiderId);
        Assert.True(payload.HasOccurredAtMs);
        saved.Status = RentalStatus.Completed;
        saved.EndDate = DateTime.UtcNow;
        await repository.UpdateRentalAsync(saved);
        await context.Database.GetCollection<Rental>("Rentals").UpdateOneAsync(r => r._id == rental._id,
            Builders<Rental>.Update.PullFilter(r => r.PendingEvents, e => e.Id == start.Id));
        var closed = await repository.GetRentalByIdAsync(rental._id.Value.ToString());
        Assert.Equal("rental.closed", Assert.Single(closed.PendingEvents).Topic);
    }
}
