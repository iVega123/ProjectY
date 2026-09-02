using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using RentalOperations.Services.RabbitMQService;
using RentalOperations.Configurations;
using RentalOperations.Services;
using RentalOperations.Entities;
using ProjectY.Shared.Observability;

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
            _channel.QueueDeclare(queue: _licenceUpdateQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.QueueDeclare(queue: _licenceUpdatePoisonQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
        }

        public async Task StartConsuming()
        {
            ConsumeQueueAsync(_licenceUpdateQueueName, ProcessLicenceUpdate);
            await ConsumePoisonQueue(_licenceUpdatePoisonQueueName);
        }

        private void ConsumeQueueAsync(string queueName, Func<string, string, Task> processMessageFunc)
        {
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                using var activity = MessagingTraceContext.StartConsumerActivity(
                    "rabbitmq",
                    queueName,
                    ea.BasicProperties.Headers,
                    ea.BasicProperties.MessageId);
                try
                {
                    await processMessageFunc(message, GetMessageId(ea.BasicProperties, body));
                    _channel.BasicAck(ea.DeliveryTag, false);
                }
                catch (Exception ex)
                {
                    MessagingTraceContext.RecordException(activity, ex);
                    _channel.BasicNack(ea.DeliveryTag, false, true);
                    _logger.LogError(ex, "Error processing message from {QueueName}.", queueName);
                }
            };

            _channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
            _logger.LogInformation($"Started consuming messages from {queueName}.");
        }


        private async Task ProcessLicenceUpdate(string message, string messageId)
        {
            var licenceInfo = JsonSerializer.Deserialize<LicencePlateRabbitMQEntity>(message)
                ?? throw new InvalidOperationException("The licence update message is empty.");

            using var scope = _serviceProvider.CreateScope();
            var inbox = scope.ServiceProvider.GetRequiredService<MongoInboxProcessor>();
            var rentalService = scope.ServiceProvider.GetRequiredService<IRentalService>();
            await inbox.ProcessAsync(
                messageId,
                "rental-operations/licence-update/v1",
                _ => rentalService.UpdateMotorcycleLicensePlateAsync(
                    licenceInfo.oldLicencePlate,
                    licenceInfo.newLicencePlate));
        }

        public async Task ConsumePoisonQueue(string poisonQueueName)
        {
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (model, ea) =>
            {
                var retriesHeader = ea.BasicProperties.Headers?.ContainsKey("x-retries") ?? false
                    ? Convert.ToInt32(ea.BasicProperties.Headers["x-retries"])
                    : 0;

                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                using var activity = MessagingTraceContext.StartConsumerActivity(
                    "rabbitmq",
                    poisonQueueName,
                    ea.BasicProperties.Headers,
                    ea.BasicProperties.MessageId);

                try
                {
                    await ProcessLicenceUpdate(message, GetMessageId(ea.BasicProperties, body));
                    _channel.BasicAck(ea.DeliveryTag, false);
                }
                catch (Exception ex)
                {
                    MessagingTraceContext.RecordException(activity, ex);
                    _logger.LogError(
                        ex,
                        "Error processing poison message from {QueueName}.",
                        poisonQueueName);
                    if (retriesHeader < 3)
                    {
                        ScheduleRetry(message, retriesHeader + 1, ea.BasicProperties);
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

        private void ScheduleRetry(
            string message,
            int retryCount,
            IBasicProperties originalProperties)
        {
            var delay = (int)Math.Pow(2, retryCount) * 1000; // Exponential backoff, e.g., 2s, 4s, 8s
            var properties = _channel.CreateBasicProperties();
            properties.Headers = originalProperties.Headers is null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(originalProperties.Headers);
            properties.Headers["x-retries"] = retryCount;
            properties.MessageId = originalProperties.MessageId;
            properties.Type = originalProperties.Type;
            properties.Persistent = true;
            MessagingTraceContext.InjectCurrent(properties.Headers);
            properties.Expiration = delay.ToString();

            _channel.QueueDeclare($"retry-poison-{retryCount}", durable: true, exclusive: false, autoDelete: false);
            _channel.BasicPublish("", $"retry-poison-{retryCount}", properties, Encoding.UTF8.GetBytes(message));
        }

        private void MoveToPoisonQueue(string message, string poisonQueueName)
        {
            _channel.QueueDeclare(queue: poisonQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.BasicPublish(exchange: "", routingKey: poisonQueueName, basicProperties: null, body: Encoding.UTF8.GetBytes(message));
        }

        private static string GetMessageId(IBasicProperties properties, byte[] body)
            => string.IsNullOrWhiteSpace(properties.MessageId)
                ? Convert.ToHexString(SHA256.HashData(body))
                : properties.MessageId;

        public void Dispose()
        {
            _channel?.Close();
            _channel?.Dispose();
            _logger.LogInformation("RabbitMQ channel closed.");
        }
    }
}
