using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using RentalOperations.Services.RabbitMQService;
using RentalOperations.Configurations;
using RentalOperations.Services;
using RentalOperations.Entities;

namespace RentalOperations.Services.RabbitMQService
{
    public class MessagingConsumerService : IMessagingConsumerService, IDisposable
    {
        private readonly IModel _channel;
        private readonly ILogger<MessagingConsumerService> _logger;
        private readonly IConnection _connection;
        private readonly string _licenceUpdateQueueName;
        private readonly string _licenceUpdatePoisonQueueName;
        private readonly IServiceProvider _serviceProvider;
        private ConcurrentDictionary<string, int> licencePlateRetryCounts = new ConcurrentDictionary<string, int>();

        public MessagingConsumerService(IRabbitMqService mqService,
            ILogger<MessagingConsumerService> logger, 
            RabbitMQOptions options,
            IServiceProvider serviceProvider)
        {
            _connection = mqService.CreateChannel();
            _logger = logger;
            _licenceUpdateQueueName = options.LicenceUpdateQueueName;
            _licenceUpdatePoisonQueueName = options.LicenceUpdatePoisonQueueName;
            _channel = _connection.CreateModel();
            InitializeQueues();
            _serviceProvider = serviceProvider;
        }

        private void InitializeQueues()
        {
            _channel.QueueDeclare(queue: _licenceUpdateQueueName, durable: false, exclusive: false);
            _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
        }

        public async Task StartConsuming()
        {
            ConsumeQueueAsync(_licenceUpdateQueueName, ProcessRiderInfo);
            ConsumePoisonQueue(_licenceUpdatePoisonQueueName);
            await Task.CompletedTask;
        }

        private void ConsumeQueueAsync(string queueName, Func<string, Task> processMessageFunc)
        {
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                try
                {
                    await processMessageFunc(message);
                    _channel.BasicAck(ea.DeliveryTag, false);
                }
                catch (Exception ex)
                {
                    _channel.BasicNack(ea.DeliveryTag, false, true);
                    _logger.LogError($"Error processing message: {ex.Message}", ex);
                }
            };

            _channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
            _logger.LogInformation($"Started consuming messages from {queueName}.");
        }


        private async Task ProcessRiderInfo(string message)
        {
            var licenceInfo = JsonSerializer.Deserialize<LicencePlateRabbitMQEntity>(message);
            string messageId = licenceInfo.newLicencePlate;

            if (!licencePlateRetryCounts.TryGetValue(messageId, out int currentRetryCount))
            {
                currentRetryCount = 0;
            }

            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var rentalService = scope.ServiceProvider.GetRequiredService<IRentalService>();
                    await rentalService.UpdateMotorcycleLicensePlateAsync(licenceInfo.oldLicencePlate, licenceInfo.newLicencePlate);
                    licencePlateRetryCounts.TryRemove(messageId, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing rider info: {ex.Message}", ex);
                currentRetryCount++;
                if (currentRetryCount >= 3)
                {
                    _logger.LogError($"Max retry attempts exceeded for message {messageId}. Moving to poison queue.");
                    MoveToPoisonQueue(message, _licenceUpdatePoisonQueueName);
                    licencePlateRetryCounts.TryRemove(messageId, out _);
                }
                else
                {
                    licencePlateRetryCounts[messageId] = currentRetryCount;
                }
            }
        }

        public async Task ConsumePoisonQueue(string poisonQueueName)
        {
            var consumer = new EventingBasicConsumer(_channel);
            consumer.Received += async (model, ea) =>
            {
                var retriesHeader = ea.BasicProperties.Headers?.ContainsKey("x-retries") ?? false
                    ? Convert.ToInt32(ea.BasicProperties.Headers["x-retries"])
                    : 0;

                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);

                try
                {
                    await ProcessRiderInfo(message);
                    _channel.BasicAck(ea.DeliveryTag, false);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error processing poison message: {ex.Message}", ex);
                    if (retriesHeader < 3)
                    {
                        ScheduleRetry(message, retriesHeader + 1);
                    }
                    else
                    {
                        _logger.LogError($"Message {ea.BasicProperties.MessageId} dropped after 3 retries.");
                    }
                    _channel.BasicNack(ea.DeliveryTag, false, false);
                }
            };

            _channel.BasicConsume(queue: poisonQueueName, autoAck: false, consumer: consumer);
        }

        private void ScheduleRetry(string message, int retryCount)
        {
            var delay = (int)Math.Pow(2, retryCount) * 1000; // Exponential backoff, e.g., 2s, 4s, 8s
            var properties = _channel.CreateBasicProperties();
            properties.Headers = new Dictionary<string, object> { { "x-retries", retryCount } };
            properties.Expiration = delay.ToString();

            _channel.QueueDeclare($"retry-poison-{retryCount}", durable: true, exclusive: false, autoDelete: false);
            _channel.BasicPublish("", $"retry-poison-{retryCount}", properties, Encoding.UTF8.GetBytes(message));
        }

        private void MoveToPoisonQueue(string message, string poisonQueueName)
        {
            _channel.QueueDeclare(queue: poisonQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.BasicPublish(exchange: "", routingKey: poisonQueueName, basicProperties: null, body: Encoding.UTF8.GetBytes(message));
        }

        public void Dispose()
        {
            _channel?.Close();
            _channel?.Dispose();
            _logger.LogInformation("RabbitMQ channel closed.");
        }
    }
}
