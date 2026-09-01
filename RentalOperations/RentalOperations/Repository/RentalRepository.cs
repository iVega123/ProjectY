using MongoDB.Bson;
using MongoDB.Driver;
using RentalOperations.Data;
using RentalOperations.Domain;
using RentalOperations.Model;

using ProjectY.Shared.Pagination;

namespace RentalOperations.Repository
{
    public class RentalRepository : IRentalRepository
    {
        private readonly IMongoCollection<Rental> _rentals;
        private readonly IMongoCollection<MotorcycleClaim> _motorcycleClaims;

        public RentalRepository(MongoDbContext context)
        {
            _rentals = context.Database.GetCollection<Rental>("Rentals");
            _motorcycleClaims = context.Database.GetCollection<MotorcycleClaim>("MotorcycleClaims");
        }

        public async Task<Rental> CreateRentalAsync(Rental rental)
        {
            try
            {
                await _rentals.InsertOneAsync(rental);
            }
            catch (MongoWriteException exception)
                when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new ActiveRentalConflictException(rental.MotorcycleLicencePlate, exception);
            }

            return rental;
        }

        public async Task<Rental> GetRentalByIdAsync(string id)
        {
            var objectId = ObjectId.Parse(id);
            return await _rentals.Find(r => r._id == objectId).FirstOrDefaultAsync();
        }

        public async Task<CursorPage<Rental>> GetRentalsByUserId(
            string userId,
            string? cursor,
            int? pageSize)
        {
            var size = CursorPagination.NormalizePageSize(pageSize);
            var cursorValue = CursorPagination.Decode(cursor);
            var filter = Builders<Rental>.Filter.Eq(rental => rental.UserId, userId);
            if (cursorValue is not null)
            {
                if (!ObjectId.TryParse(cursorValue, out var afterId))
                {
                    throw new FormatException("The pagination cursor is invalid.");
                }

                filter &= Builders<Rental>.Filter.Gt(rental => rental._id, afterId);
            }

            var fetched = await _rentals.Find(filter)
                .SortBy(rental => rental._id)
                .Limit(size + 1)
                .ToListAsync();
            return CursorPagination.CreatePage(
                fetched,
                size,
                rental => rental._id!.Value.ToString());
        }

        public Task<bool> HasOverlappingRentalAsync(
            string licencePlate,
            DateTime startDate,
            DateTime endDate)
        {
            var filter = Builders<Rental>.Filter.And(
                Builders<Rental>.Filter.Eq(rental => rental.MotorcycleLicencePlate, licencePlate),
                Builders<Rental>.Filter.Eq(rental => rental.Status, RentalStatus.Active),
                Builders<Rental>.Filter.Lt(rental => rental.StartDate, endDate),
                Builders<Rental>.Filter.Gt(rental => rental.PredictedEndDate, startDate));
            return _rentals.Find(filter).Limit(1).AnyAsync();
        }

        public async Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate)
        {
            var today = DateTime.UtcNow;
            var filter = Builders<Rental>.Filter.And(
                Builders<Rental>.Filter.Eq(rental => rental.MotorcycleLicencePlate, licencePlate),
                Builders<Rental>.Filter.Eq(rental => rental.Status, RentalStatus.Active),
                Builders<Rental>.Filter.Lte(rental => rental.StartDate, today),
                Builders<Rental>.Filter.Gte(rental => rental.PredictedEndDate, today));
            return await _rentals.Find(filter).Limit(1).AnyAsync();
        }

        public async Task UpdateRentalAsync(Rental rental)
        {
            await _rentals.ReplaceOneAsync(r => r._id == rental._id, rental);
        }

        public async Task DeleteRentalAsync(string id)
        {
            var objectId = ObjectId.Parse(id);
            await _rentals.DeleteOneAsync(r => r._id == objectId);
        }

        public async Task UpdateLicensePlateForAllRentalsAsync(string oldLicensePlate, string newLicensePlate)
        {
            var activeClaim = await _motorcycleClaims
                .Find(claim => claim.MotorcycleLicencePlate == oldLicensePlate &&
                               claim.Kind == MotorcycleClaimKind.ActiveRental)
                .FirstOrDefaultAsync();
            if (activeClaim is not null)
            {
                var claimResult = await TryClaimRentalAsync(newLicensePlate, activeClaim.RentalId!);
                if (claimResult != MotorcycleClaimResult.Acquired)
                {
                    throw new InvalidOperationException(
                        $"Cannot move the active rental claim to licence plate {newLicensePlate}.");
                }
            }

            var filter = Builders<Rental>.Filter.Eq(r => r.MotorcycleLicencePlate, oldLicensePlate);
            var update = Builders<Rental>.Update.Set(r => r.MotorcycleLicencePlate, newLicensePlate);

            var updateResult = await _rentals.UpdateManyAsync(filter, update);
            if (activeClaim is not null)
            {
                await ReleaseRentalClaimAsync(oldLicensePlate, activeClaim.RentalId!);
            }

            Console.WriteLine($"{updateResult.ModifiedCount} rentals updated.");
        }

        public Task<MotorcycleClaimResult> TryClaimRentalAsync(string licencePlate, string rentalId) =>
            TryAcquireClaimAsync(new MotorcycleClaim
            {
                MotorcycleLicencePlate = licencePlate,
                Kind = MotorcycleClaimKind.ActiveRental,
                RentalId = rentalId,
                CreatedAtUtc = DateTime.UtcNow
            });

        public Task<MotorcycleClaimResult> TryClaimRetirementAsync(string licencePlate) =>
            TryAcquireClaimAsync(new MotorcycleClaim
            {
                MotorcycleLicencePlate = licencePlate,
                Kind = MotorcycleClaimKind.Retired,
                CreatedAtUtc = DateTime.UtcNow
            });

        public async Task ReleaseRentalClaimAsync(string licencePlate, string rentalId)
        {
            await _motorcycleClaims.DeleteOneAsync(claim =>
                claim.MotorcycleLicencePlate == licencePlate &&
                claim.Kind == MotorcycleClaimKind.ActiveRental &&
                claim.RentalId == rentalId);
        }

        private async Task<MotorcycleClaimResult> TryAcquireClaimAsync(MotorcycleClaim claim)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await _motorcycleClaims.InsertOneAsync(claim);
                    return MotorcycleClaimResult.Acquired;
                }
                catch (MongoWriteException exception)
                    when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    var existing = await _motorcycleClaims
                        .Find(candidate => candidate.MotorcycleLicencePlate == claim.MotorcycleLicencePlate)
                        .FirstOrDefaultAsync();

                    if (existing?.Kind == MotorcycleClaimKind.Retired)
                    {
                        return MotorcycleClaimResult.Retired;
                    }

                    if (existing?.Kind == MotorcycleClaimKind.ActiveRental)
                    {
                        return existing.RentalId == claim.RentalId
                            ? MotorcycleClaimResult.Acquired
                            : MotorcycleClaimResult.ActiveRental;
                    }
                }
            }

            throw new InvalidOperationException(
                $"Motorcycle claim for {claim.MotorcycleLicencePlate} changed too quickly to resolve.");
        }
    }
}
