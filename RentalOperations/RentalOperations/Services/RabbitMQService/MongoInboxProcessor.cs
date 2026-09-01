using MongoDB.Driver;
using RentalOperations.Data;
using RentalOperations.Model;

namespace RentalOperations.Services.RabbitMQService;

public sealed class MongoInboxOptions
{
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
}

public sealed class InboxMessageInProgressException : Exception
{
    public InboxMessageInProgressException(string messageId)
        : base($"Inbox message {messageId} is already being processed.")
    {
    }
}

public sealed class MongoInboxProcessor
{
    public const string CompletedStatus = "completed";
    private const string ProcessingStatus = "processing";

    private readonly IMongoCollection<InboxMessage> _messages;
    private readonly MongoInboxOptions _options;
    private readonly TimeProvider _timeProvider;

    public MongoInboxProcessor(
        MongoDbContext context,
        MongoInboxOptions options,
        TimeProvider timeProvider)
    {
        _messages = context.Database.GetCollection<InboxMessage>("InboxMessages");
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<bool> ProcessAsync(
        string messageId,
        string consumerName,
        Func<CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentNullException.ThrowIfNull(handler);

        var id = $"{consumerName}:{messageId}";
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var claimToken = Guid.NewGuid().ToString("D");
        var claimable = Builders<InboxMessage>.Filter.And(
            Builders<InboxMessage>.Filter.Eq(message => message.Id, id),
            Builders<InboxMessage>.Filter.Ne(message => message.Status, CompletedStatus),
            Builders<InboxMessage>.Filter.Or(
                Builders<InboxMessage>.Filter.Eq(message => message.ClaimedUntilUtc, null),
                Builders<InboxMessage>.Filter.Lt(message => message.ClaimedUntilUtc, now)));
        var claim = Builders<InboxMessage>.Update
            .SetOnInsert(message => message.Id, id)
            .SetOnInsert(message => message.MessageId, messageId)
            .SetOnInsert(message => message.ConsumerName, consumerName)
            .Set(message => message.Status, ProcessingStatus)
            .Set(message => message.ClaimToken, claimToken)
            .Set(message => message.ClaimedUntilUtc, now + _options.ClaimLease);

        InboxMessage? claimed = null;
        try
        {
            claimed = await _messages.FindOneAndUpdateAsync(
                claimable,
                claim,
                new FindOneAndUpdateOptions<InboxMessage>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code == 11000)
        {
            // Another replica owns the existing row, or it has already completed.
        }

        if (claimed is null)
        {
            var existing = await _messages.Find(message => message.Id == id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing?.Status == CompletedStatus)
            {
                return false;
            }

            throw new InboxMessageInProgressException(messageId);
        }

        try
        {
            await handler(cancellationToken);
            var completion = await _messages.UpdateOneAsync(
                message => message.Id == id && message.ClaimToken == claimToken,
                Builders<InboxMessage>.Update
                    .Set(message => message.Status, CompletedStatus)
                    .Set(message => message.ProcessedAtUtc, _timeProvider.GetUtcNow().UtcDateTime)
                    .Set(message => message.ClaimToken, null)
                    .Set(message => message.ClaimedUntilUtc, null),
                cancellationToken: cancellationToken);
            if (completion.ModifiedCount != 1)
            {
                throw new InboxMessageInProgressException(messageId);
            }

            return true;
        }
        catch
        {
            await _messages.UpdateOneAsync(
                message => message.Id == id && message.ClaimToken == claimToken,
                Builders<InboxMessage>.Update
                    .Set(message => message.ClaimToken, null)
                    .Set(message => message.ClaimedUntilUtc, DateTime.UnixEpoch),
                cancellationToken: CancellationToken.None);
            throw;
        }
    }
}
