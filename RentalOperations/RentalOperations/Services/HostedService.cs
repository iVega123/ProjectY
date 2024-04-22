using RentalOperations.Services.RabbitMQService;

namespace RentalOperations.Services
{
    public class ConsumerHostedService : BackgroundService
    {
        private readonly IMessagingConsumerService _consumerService;

        public ConsumerHostedService(IMessagingConsumerService consumerService)
        {
            _consumerService = consumerService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _consumerService.StartConsuming();
        }
    }
}
