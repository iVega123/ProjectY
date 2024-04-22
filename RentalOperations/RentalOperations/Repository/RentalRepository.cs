using MongoDB.Bson;
using MongoDB.Driver;
using RentalOperations.Data;
using RentalOperations.Model;

namespace RentalOperations.Repository
{
    public class RentalRepository:IRentalRepository
    {
        private readonly IMongoCollection<Rental> _rentals;

        public RentalRepository(MongoDbContext context)
        {
            _rentals = context.Database.GetCollection<Rental>("Rentals");
        }

        public async Task<Rental> CreateRentalAsync(Rental rental)
        {
            await _rentals.InsertOneAsync(rental);
            return rental;
        }

        public async Task<Rental> GetRentalByIdAsync(string id)
        {
            var objectId = ObjectId.Parse(id);
            return await _rentals.Find(r => r._id == objectId).FirstOrDefaultAsync();
        }

        public async Task<List<Rental>> GetRentalsByUserId(string userId)
        {
            return await _rentals.Find(r => r.UserId == userId).ToListAsync();
        }

        public async Task<List<Rental>> GetRentalsByMotorcycleIdAsync(string licencePlate)
        {
            return await _rentals.Find(r => r.MotorcycleLicencePlate == licencePlate).ToListAsync();
        }

        public async Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate)
        {
            var today = DateTime.UtcNow;
            var rentals = await _rentals.Find(r => r.MotorcycleLicencePlate == licencePlate).ToListAsync();

            return rentals.Any(r => r.StartDate <= today && (r.EndDate > r.StartDate ? r.EndDate : r.PredictedEndDate) >= today);
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
            var filter = Builders<Rental>.Filter.Eq(r => r.MotorcycleLicencePlate, oldLicensePlate);
            var update = Builders<Rental>.Update.Set(r => r.MotorcycleLicencePlate, newLicensePlate);

            var updateResult = await _rentals.UpdateManyAsync(filter, update);

            Console.WriteLine($"{updateResult.ModifiedCount} rentals updated.");
        }
    }
}
