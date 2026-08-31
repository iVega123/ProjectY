using MongoDB.Driver;
using RentalOperations.Model;
using RentalOperations.Services.RabbitMQService;

namespace RentalOperations.Data;

public sealed class MongoInboxInitializer : IHostedService
{
    public const string RetentionIndexName = "ix_inbox_processed_retention";

    private readonly MongoDbContext _context;
    private readonly MongoInboxOptions _options;

    public MongoInboxInitializer(MongoDbContext context, MongoInboxOptions options)
    {
        _context = context;
        _options = options;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var messages = _context.Database.GetCollection<InboxMessage>("InboxMessages");
        var retentionIndex = new CreateIndexModel<InboxMessage>(
            Builders<InboxMessage>.IndexKeys.Ascending(message => message.ProcessedAtUtc),
            new CreateIndexOptions<InboxMessage>
            {
                Name = RetentionIndexName,
                ExpireAfter = _options.RetentionPeriod
            });

        await messages.Indexes.CreateOneAsync(retentionIndex, cancellationToken: cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
