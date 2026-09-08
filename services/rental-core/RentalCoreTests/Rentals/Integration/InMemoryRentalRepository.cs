using ProjectY.Shared.Pagination;
using RentalOperations.Domain;
using RentalOperations.Model;
using RentalOperations.Repository;

namespace RentalOperationsTests.Integration;

/// <summary>
/// O armazenamento fora do caminho, para os testes que são sobre outra coisa.
///
/// A pipeline de autorização não deveria precisar de um banco de pé para ser
/// exercitada, e as garantias de armazenamento têm os seus próprios testes,
/// contra o schema real, em Rentals/Integration/Database. Este substituto
/// existe só para que o processo suba.
/// </summary>
public sealed class InMemoryRentalRepository : IRentalRepository
{
    private readonly List<Rental> _rentals = [];
    private readonly Lock _gate = new();

    public Rental SeedRental(Rental rental)
    {
        lock (_gate)
        {
            if (rental.Id == Guid.Empty)
            {
                rental.Id = Guid.NewGuid();
            }

            _rentals.RemoveAll(existing => existing.Id == rental.Id);
            _rentals.Add(rental);
            return rental;
        }
    }

    public Rental? FindRental(string id)
    {
        lock (_gate)
        {
            return Guid.TryParse(id, out var rentalId)
                ? _rentals.FirstOrDefault(rental => rental.Id == rentalId)
                : null;
        }
    }

    public Task<Rental> CreateRentalAsync(Rental rental, CancellationToken token = default)
    {
        lock (_gate)
        {
            if (rental.Id == Guid.Empty)
            {
                rental.Id = Guid.NewGuid();
            }

            if (_rentals.Any(existing =>
                    existing.MotorcycleId == rental.MotorcycleId &&
                    existing.Status == RentalStatus.Active))
            {
                throw new ActiveRentalConflictException(rental.MotorcycleId);
            }

            _rentals.Add(rental);
            return Task.FromResult(rental);
        }
    }

    public Task<Rental?> GetRentalByIdAsync(string id, CancellationToken token = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Guid.TryParse(id, out var rentalId)
                ? _rentals.FirstOrDefault(rental => rental.Id == rentalId)
                : null);
        }
    }

    public Task<CursorPage<Rental>> GetRentalsByUserId(
        string userId,
        string? cursor,
        int? pageSize,
        CancellationToken token = default)
    {
        lock (_gate)
        {
            var size = CursorPagination.NormalizePageSize(pageSize);
            var after = CursorPagination.Decode(cursor);
            var ordered = _rentals
                .Where(rental => rental.UserId == userId)
                .OrderBy(rental => rental.Id.ToString(), StringComparer.Ordinal)
                .Where(rental => after is null ||
                    string.CompareOrdinal(rental.Id.ToString(), after) > 0)
                .Take(size + 1)
                .ToList();
            return Task.FromResult(CursorPagination.CreatePage(
                ordered, size, rental => rental.Id.ToString()));
        }
    }

    public Task<bool> HasOverlappingRentalAsync(
        Guid motorcycleId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken token = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rentals
                .Where(rental => rental.MotorcycleId == motorcycleId)
                .Any(rental => RentalPeriod.Overlaps(rental, startDate, endDate)));
        }
    }

    public Task<IReadOnlyList<Rental>> GetRentalsByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken token = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Rental>>(
                _rentals.Where(rental => ids.Contains(rental.Id)).ToList());
        }
    }

    public Task<bool> IsMotorcycleCurrentlyRentedAsync(
        Guid motorcycleId,
        CancellationToken token = default)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            return Task.FromResult(_rentals.Any(rental =>
                rental.MotorcycleId == motorcycleId &&
                rental.Status == RentalStatus.Active &&
                rental.StartDate <= now &&
                rental.PredictedEndDate >= now));
        }
    }

    public Task UpdateRentalAsync(Rental rental, CancellationToken token = default)
    {
        lock (_gate)
        {
            var index = _rentals.FindIndex(candidate => candidate.Id == rental.Id);
            if (index < 0)
            {
                throw new RentalSettlementConflictException();
            }

            _rentals[index] = rental;
            return Task.CompletedTask;
        }
    }
}
