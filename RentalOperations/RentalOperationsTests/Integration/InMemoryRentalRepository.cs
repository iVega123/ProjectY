using MongoDB.Bson;
using RentalOperations.Domain;
using RentalOperations.Model;
using RentalOperations.Repository;
using ProjectY.Shared.Pagination;
using System.Collections.Concurrent;

namespace RentalOperationsTests.Integration;

public sealed class InMemoryRentalRepository : IRentalRepository
{
    private readonly ConcurrentDictionary<ObjectId, Rental> _rentals = new();
    private readonly ConcurrentDictionary<string, (MotorcycleClaimKind Kind, string? RentalId)> _claims = new();

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

    public Task<CursorPage<Rental>> GetRentalsByUserId(
        string userId,
        string? cursor,
        int? pageSize)
    {
        var normalizedPageSize = CursorPagination.NormalizePageSize(pageSize);
        var after = CursorPagination.Decode(cursor);
        var rentals = _rentals.Values
            .Where(rental => rental.UserId == userId)
            .Where(rental => after is null || rental._id > ObjectId.Parse(after))
            .OrderBy(rental => rental._id)
            .Take(normalizedPageSize + 1)
            .Select(Clone)
            .ToList();
        return Task.FromResult(CursorPagination.CreatePage(
            rentals,
            normalizedPageSize,
            rental => rental._id!.Value.ToString()));
    }

    public Task<bool> HasOverlappingRentalAsync(
        string licencePlate,
        DateTime startDate,
        DateTime endDate) =>
        Task.FromResult(_rentals.Values.Any(rental =>
            rental.MotorcycleLicencePlate == licencePlate &&
            rental.Status is not RentalStatus.Cancelled and not RentalStatus.Quarantined &&
            rental.StartDate < endDate &&
            (rental.EndDate ?? rental.PredictedEndDate) > startDate));

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

    public Task<MotorcycleClaimResult> TryClaimRentalAsync(string licencePlate, string rentalId)
    {
        if (_claims.TryAdd(licencePlate, (MotorcycleClaimKind.ActiveRental, rentalId)))
        {
            return Task.FromResult(MotorcycleClaimResult.Acquired);
        }

        var existing = _claims[licencePlate];
        return Task.FromResult(existing.Kind == MotorcycleClaimKind.Retired
            ? MotorcycleClaimResult.Retired
            : existing.RentalId == rentalId
                ? MotorcycleClaimResult.Acquired
                : MotorcycleClaimResult.ActiveRental);
    }

    public Task<MotorcycleClaimResult> TryClaimRetirementAsync(string licencePlate)
    {
        if (_claims.TryAdd(licencePlate, (MotorcycleClaimKind.Retired, null)))
        {
            return Task.FromResult(MotorcycleClaimResult.Acquired);
        }

        return Task.FromResult(_claims[licencePlate].Kind == MotorcycleClaimKind.Retired
            ? MotorcycleClaimResult.Retired
            : MotorcycleClaimResult.ActiveRental);
    }

    public Task ReleaseRentalClaimAsync(string licencePlate, string rentalId)
    {
        _claims.TryRemove(
            new KeyValuePair<string, (MotorcycleClaimKind Kind, string? RentalId)>(
                licencePlate,
                (MotorcycleClaimKind.ActiveRental, rentalId)));
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
