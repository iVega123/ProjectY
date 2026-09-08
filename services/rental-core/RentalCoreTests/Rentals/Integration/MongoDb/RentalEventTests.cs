using MongoDB.Driver;
using MongoDB.Bson;
using ProjectY.Events;
using RentalOperations.Data;
using RentalOperations.Model;
using RentalOperations.Repository;
using RentalOperations.Services;
using RentalOperations.Domain;
using AutoMapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Security.Claims;
using RentalOperations.Controllers;
using RentalOperations.CrossCutting.Services;
using RentalOperations.DTOs;
using RentalOperations.Mapper;
using Testcontainers.MongoDb;

namespace RentalOperationsTests.Integration.MongoDb;

public sealed class RentalEventTests : IAsyncLifetime
{
    private readonly MongoDbContainer database = new MongoDbBuilder("mongo:8.0").Build();
    public Task InitializeAsync() => database.StartAsync();
    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [Fact]
    public async Task ConcurrentSettlements_ReturnOnlyPersistedSuccessAndConflictForLoser()
    {
        var context = new MongoDbContext(database.GetConnectionString(), "concurrent_settlements");
        var repository = new RentalRepository(context);
        var rental = new Rental
        {
            MotorcycleId = "moto-1",
            MotorcycleLicencePlate = "RACE1234",
            UserId = "rider-1",
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            PredictedEndDate = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            InitCost = 210
        };
        await repository.CreateRentalAsync(rental);
        var id = rental._id!.Value.ToString();
        await repository.TryClaimRentalAsync(rental.MotorcycleLicencePlate, id);

        // Both requests must read separate active snapshots before either writes.
        var reads = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronized = new Mock<IRentalRepository>(MockBehavior.Strict);
        synchronized.Setup(r => r.GetRentalByIdAsync(id)).Returns(async () =>
        {
            var snapshot = await repository.GetRentalByIdAsync(id);
            if (Interlocked.Increment(ref reads) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return snapshot;
        });
        synchronized.Setup(r => r.UpdateRentalAsync(It.IsAny<Rental>()))
            .Returns((Rental value) => repository.UpdateRentalAsync(value));
        synchronized.Setup(r => r.ReleaseRentalClaimAsync(rental.MotorcycleLicencePlate, id))
            .Returns(() => repository.ReleaseRentalClaimAsync(rental.MotorcycleLicencePlate, id));
        var mapper = new MapperConfiguration(c => c.AddProfile<RentalProfile>(), NullLoggerFactory.Instance).CreateMapper();
        var service = new RentalService(synchronized.Object, mapper,
            Mock.Of<IRiderProjectionStore>(), Mock.Of<IMotorcycleService>());
        RentalController Controller() => new(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, rental.UserId)], "test"))
                }
            }
        };

        var results = await Task.WhenAll(
            Controller().CalculateFinalCost(id, rental.PredictedEndDate.AddDays(-2)),
            Controller().CalculateFinalCost(id, rental.PredictedEndDate.AddDays(3)));
        var success = Assert.IsType<ResponseRentalDTO>(Assert.Single(results.OfType<OkObjectResult>()).Value);
        var conflict = Assert.Single(results.OfType<ConflictObjectResult>());
        Assert.Equal(409, Assert.IsType<ProblemDetails>(conflict.Value).Status);
        synchronized.Verify(r => r.ReleaseRentalClaimAsync(rental.MotorcycleLicencePlate, id), Times.Once);
        var stored = await repository.GetRentalByIdAsync(id);
        Assert.Equal(stored.EndDate, success.ActualEndDate);
        Assert.Equal(stored.FinalCost, success.FinalTotalCost);
        Assert.Equal(stored.AdditionalCostsOrSavings, success.AdditionalCostsOrSavings);
        Assert.Equal(stored.StatusMessage, success.StatusMessage);
        Assert.Single(stored.PendingEvents.Where(e => e.Topic == "rental.closed"));

        // A later retry reads the committed settlement, regardless of its supplied date.
        var retry = Assert.IsType<OkObjectResult>(await Controller().CalculateFinalCost(id, rental.PredictedEndDate.AddDays(9)));
        var replay = Assert.IsType<ResponseRentalDTO>(retry.Value);
        Assert.Equal(success.ActualEndDate, replay.ActualEndDate);
        Assert.Equal(success.FinalTotalCost, replay.FinalTotalCost);
    }

    [Fact]
    public async Task LegacyBackfillIsStableAcrossReplicasAcknowledgementAndConcurrentClose()
    {
        var context = new MongoDbContext(database.GetConnectionString(), "legacy_events");
        var rentals = context.Database.GetCollection<Rental>("Rentals");
        var raw = context.Database.GetCollection<BsonDocument>("Rentals");
        var rental = new Rental
        {
            MotorcycleLicencePlate = "OLD1234",
            UserId = "legacy-rider",
            StartDate = DateTime.UtcNow.AddDays(-2),
            PredictedEndDate = DateTime.UtcNow.AddDays(5)
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
        await Assert.ThrowsAsync<RentalSettlementConflictException>(() => repository.UpdateRentalAsync(stale));
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
