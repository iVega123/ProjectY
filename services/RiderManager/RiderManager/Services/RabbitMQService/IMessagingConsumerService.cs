namespace RiderManager.Services.RabbitMQService
{
    public interface IMessagingConsumerService
    {
        Task StartConsuming();
    }
}
