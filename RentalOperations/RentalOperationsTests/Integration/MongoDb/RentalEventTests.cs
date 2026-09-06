using MongoDB.Driver;
using ProjectY.Events;
using RentalOperations.Data;
using RentalOperations.Model;
using RentalOperations.Repository;
using Testcontainers.MongoDb;

namespace RentalOperationsTests.Integration.MongoDb;

public sealed class RentalEventTests : IAsyncLifetime
{
    private readonly MongoDbContainer database = new MongoDbBuilder("mongo:8.0").Build();
    public Task InitializeAsync() => database.StartAsync();
    public Task DisposeAsync() => database.DisposeAsync().AsTask();

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
