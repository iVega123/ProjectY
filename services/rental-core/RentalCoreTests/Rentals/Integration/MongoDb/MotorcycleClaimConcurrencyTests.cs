using MongoDB.Driver;
using RentalOperations.Data;
using RentalOperations.Domain;
using RentalOperations.Model;
using RentalOperations.Repository;
using Testcontainers.MongoDb;

namespace RentalOperationsTests.Integration.MongoDb;

public sealed class MotorcycleClaimConcurrencyTests : IAsyncLifetime
{
    private const string DatabaseName = "motorcycle_claim_race_tests";
    private readonly MongoDbContainer _database = new MongoDbBuilder("mongo:8.0").Build();

    public Task InitializeAsync() => _database.StartAsync();

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentRentalAndRetirement_ExactlyOneClaimWinsCleanly()
    {
        var context = new MongoDbContext(_database.GetConnectionString(), DatabaseName);
        var repository = new RentalRepository(context);
        const string licensePlate = "RACE-RET-0001";
        var rentalId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        using var ready = new CountdownEvent(2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<MotorcycleClaimResult> CompeteAsync(Func<Task<MotorcycleClaimResult>> attempt)
        {
            ready.Signal();
            await start.Task;
            return await attempt();
        }

        var rentalAttempt = CompeteAsync(() => repository.TryClaimRentalAsync(licensePlate, rentalId));
        var retirementAttempt = CompeteAsync(() => repository.TryClaimRetirementAsync(licensePlate));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        start.SetResult();

        var results = await Task.WhenAll(rentalAttempt, retirementAttempt);

        Assert.Single(results, result => result == MotorcycleClaimResult.Acquired);
        Assert.Single(results, result => result is MotorcycleClaimResult.ActiveRental or MotorcycleClaimResult.Retired);
        var claims = context.Database.GetCollection<MotorcycleClaim>("MotorcycleClaims");
        Assert.Equal(1, await claims.CountDocumentsAsync(FilterDefinition<MotorcycleClaim>.Empty));
    }
}
