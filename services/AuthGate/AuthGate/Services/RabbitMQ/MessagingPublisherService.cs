using AuthGate.Configurations;
using AuthGate.Data;
using AuthGate.Entities;
using ProjectY.Shared.Messaging;

namespace AuthGate.Services.RabbitMQ;

public sealed class MessagingPublisherService : IMessagingPublisherService
{
    private readonly ApplicationDbContext _context;
    private readonly RabbitMQOptions _rabbitmqOptions;
    private readonly QueueMessageAuthenticator _messageAuthenticator;

    public MessagingPublisherService(
        ApplicationDbContext context,
        RabbitMQOptions rabbitMQOptions,
        QueueMessageAuthenticator messageAuthenticator)
    {
        _context = context;
        _rabbitmqOptions = rabbitMQOptions;
        _messageAuthenticator = messageAuthenticator;
    }

    public void PublishImageStream(Stream imageStream, string extension, string userId)
    {
        const int bufferSize = 4096;
        var buffer = new byte[bufferSize];
        var sequenceNumber = 1L;
        var fileName = $"{userId}_{DateTime.UtcNow:yyyyMMddHHmmss}{extension}";
        int byteCount;

        while ((byteCount = imageStream.Read(buffer, 0, bufferSize)) > 0)
        {
            var message = new
            {
                UserId = userId,
                SequenceNumber = sequenceNumber - 1,
                FileName = fileName,
                Content = Convert.ToBase64String(buffer, 0, byteCount),
                EndOfFile = imageStream.Position == imageStream.Length
            };
            var envelope = _messageAuthenticator.CreateEnvelope(
                "rider.cnh-image-part.v1",
                userId,
                message);

            _context.OutboxMessages.Add(new OutboxMessage
            {
                AggregateType = "rider",
                AggregateId = userId,
                AggregateSequence = sequenceNumber++,
                EventType = "rider.cnh-image-part.v1",
                Destination = _rabbitmqOptions.ImageStreamQueueName,
                Payload = envelope
            });
        }
    }

    public void PublishRiderInfo(RiderMQEntity rider)
    {
        var envelope = _messageAuthenticator.CreateEnvelope(
            "rider.registration.v1",
            rider.UserId,
            rider);

        _context.OutboxMessages.Add(new OutboxMessage
        {
            AggregateType = "rider",
            AggregateId = rider.UserId,
            AggregateSequence = 0,
            EventType = "rider.registration.v1",
            Destination = _rabbitmqOptions.RiderInfoQueueName,
            Payload = envelope
        });
    }
}
