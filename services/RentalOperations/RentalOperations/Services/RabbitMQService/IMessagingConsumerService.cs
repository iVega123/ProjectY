namespace RentalOperations.Services.RabbitMQService
{
    public interface IMessagingConsumerService
    {
        Task StartConsuming();
    }
}
