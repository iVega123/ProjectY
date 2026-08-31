using MongoDB.Bson;
using RentalOperations.Model;
using RentalOperations.Repository;
using System.Collections.Concurrent;

namespace RentalOperationsTests.Integration;

public sealed class InMemoryRentalRepository : IRentalRepository
{
    private readonly ConcurrentDictionary<ObjectId, Rental> _rentals = new();

    public InMemoryRentalRepository()
    {
        SeedRental(new Rental
        {
            MotorcycleLicencePlate = "DEFAULT-PLATE",
            UserId = "another-user",
            StartDate = DateTime.UtcNow.Date,
            PredictedEndDate = DateTime.UtcNow.Date.AddDays(7),
            InitCost = 210m
        });
    }

    public Rental SeedRental(Rental rental)
    {
        var stored = Clone(rental);
        var id = stored._id ?? ObjectId.GenerateNewId();
        stored._id = id;
        _rentals[id] = stored;
        return Clone(stored);
    }

    public Rental? FindRental(string id)
    {
        var objectId = ObjectId.Parse(id);
        return _rentals.TryGetValue(objectId, out var rental) ? Clone(rental) : null;
    }

    public Task<Rental> CreateRentalAsync(Rental rental) =>
        Task.FromResult(SeedRental(rental));

    public Task<Rental> GetRentalByIdAsync(string id) =>
        Task.FromResult(FindRental(id)!);

    public Task<List<Rental>> GetRentalsByUserId(string userId) =>
        Task.FromResult(_rentals.Values
            .Where(rental => rental.UserId == userId)
            .Select(Clone)
            .ToList());

    public Task<List<Rental>> GetRentalsByMotorcycleIdAsync(string licencePlate) =>
        Task.FromResult(_rentals.Values
            .Where(rental => rental.MotorcycleLicencePlate == licencePlate)
            .Select(Clone)
            .ToList());

    public Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate)
    {
        var now = DateTime.UtcNow;
        return Task.FromResult(_rentals.Values.Any(rental =>
            rental.MotorcycleLicencePlate == licencePlate &&
            rental.Status == RentalStatus.Active &&
            rental.StartDate <= now &&
            rental.PredictedEndDate >= now));
    }

    public Task UpdateRentalAsync(Rental rental)
    {
        var id = rental._id ?? throw new InvalidOperationException("Rental ID is required.");
        _rentals[id] = Clone(rental);
        return Task.CompletedTask;
    }

    public Task UpdateLicensePlateForAllRentalsAsync(string oldLicensePlate, string newLicensePlate)
    {
        foreach (var entry in _rentals.ToArray())
        {
            if (entry.Value.MotorcycleLicencePlate != oldLicensePlate)
            {
                continue;
            }

            var updated = Clone(entry.Value);
            updated.MotorcycleLicencePlate = newLicensePlate;
            _rentals[entry.Key] = updated;
        }

        return Task.CompletedTask;
    }

    public Task DeleteRentalAsync(string id)
    {
        _rentals.TryRemove(ObjectId.Parse(id), out _);
        return Task.CompletedTask;
    }

    private static Rental Clone(Rental rental) => new()
    {
        _id = rental._id,
        MotorcycleLicencePlate = rental.MotorcycleLicencePlate,
        UserId = rental.UserId,
        StartDate = rental.StartDate,
        EndDate = rental.EndDate,
        PredictedEndDate = rental.PredictedEndDate,
        InitCost = rental.InitCost,
        FinalCost = rental.FinalCost,
        AdditionalCostsOrSavings = rental.AdditionalCostsOrSavings,
        StatusMessage = rental.StatusMessage,
        Status = rental.Status
    };
}
