using MotoHub.Entities;

namespace MotoHub.Services.RabbitMQ
{
    public interface IMessagingPublisherService
    {
        void PublishLicenceUpdate(LicencePlateRabbitMQEntity licence);
    }

}
