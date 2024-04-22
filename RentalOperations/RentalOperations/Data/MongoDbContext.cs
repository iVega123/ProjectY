using MongoDB.Driver;

namespace RentalOperations.Data
{
    public class MongoDbContext
    {
        public IMongoDatabase Database { get; }

        public MongoDbContext(string connectionString, string dbName)
        {
            var client = new MongoClient(connectionString);
            Database = client.GetDatabase(dbName);
        }
    }

}
