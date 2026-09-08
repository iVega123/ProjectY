using MotoHub.Configurations;
using MotoHub.Data;
using MotoHub.Entities;
using ProjectY.Shared.Messaging;
using System.Text.Json;

namespace MotoHub.Services.RabbitMQ;

public sealed class MessagingPublisherService : IMessagingPublisherService
{
    private readonly ApplicationDbContext _context;
    private readonly RabbitMQOptions _rabbitmqOptions;

    public MessagingPublisherService(ApplicationDbContext context, RabbitMQOptions rabbitMQOptions)
    {
        _context = context;
        _rabbitmqOptions = rabbitMQOptions;
    }

    public void PublishLicenceUpdate(LicencePlateRabbitMQEntity licenceUpdate)
    {
        _context.OutboxMessages.Add(new OutboxMessage
        {
            AggregateType = "motorcycle",
            AggregateId = licenceUpdate.AggregateId,
            AggregateSequence = 0,
            EventType = "motorcycle.licence-plate-updated.v1",
            Destination = _rabbitmqOptions.LicenceUpdateQueueName,
            Payload = JsonSerializer.Serialize(licenceUpdate)
        });
    }
}
