using AuthGate.DTO;
using AuthGate.Entities;

namespace AuthGate.Services.RabbitMQ
{
    public interface IMessagingPublisherService
    {
        void PublishImageStream(Stream imageStream, string extension, string userId);
        void PublishRiderInfo(RiderMQEntity rider);
    }

}
