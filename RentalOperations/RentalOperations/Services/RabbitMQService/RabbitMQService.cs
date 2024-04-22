using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RentalOperations.Configurations;

namespace RentalOperations.Services.RabbitMQService
{
    public interface IRabbitMqService
    {
        IConnection CreateChannel();
    }

    public class RabbitMqService : IRabbitMqService
    {
        private readonly RabbitMQOptions _configuration;
        private readonly ILogger<RabbitMqService> _logger;

        public RabbitMqService(IOptions<RabbitMQOptions> options, ILogger<RabbitMqService> logger)
        {
            _configuration = options.Value;
            _logger = logger;
        }

        public IConnection CreateChannel()
        {
            var connectionFactory = new ConnectionFactory()
            {
                UserName = _configuration.UserName,
                Password = _configuration.Password,
                HostName = _configuration.HostName,
                DispatchConsumersAsync = true
            };

            try
            {
                _logger.LogInformation("Attempting to connect to RabbitMQ...");
                var connection = connectionFactory.CreateConnection();
                _logger.LogInformation("Successfully connected to RabbitMQ.");
                return connection;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to RabbitMQ.");
                throw;
            }
        }
    }
}
