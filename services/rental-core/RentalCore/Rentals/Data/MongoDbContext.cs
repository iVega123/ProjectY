using MongoDB.Driver;

namespace RentalOperations.Data
{
    public class MongoDbContext
    {
        public IMongoDatabase Database { get; }

        public MongoDbContext(string connectionString, string dbName)
        {
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
            settings.ConnectTimeout = TimeSpan.FromSeconds(1);
            settings.SocketTimeout = TimeSpan.FromSeconds(1);
            settings.WaitQueueTimeout = TimeSpan.FromSeconds(1);
            var client = new MongoClient(settings);
            Database = client.GetDatabase(dbName);
        }
    }

}
