using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using System.Text;
using RiderManager.Configurations;
using RiderManager.Entities;
using ProjectY.Shared.Messaging;

namespace RiderManager.Services.RabbitMQService
{
    public class MessagingConsumerService : IMessagingConsumerService, IDisposable
    {
        private readonly IModel _channel;
        private readonly ILogger<MessagingConsumerService> _logger;
        private readonly IConnection _connection;
        private readonly string _riderInfoQueueName;
        private readonly string _imageStreamQueueName;
        private readonly string _riderInfoPoisonQueueName;
        private readonly IServiceProvider _serviceProvider;
        private readonly QueueMessageAuthenticator _messageAuthenticator;

        public MessagingConsumerService(IRabbitMqService mqService,
            ILogger<MessagingConsumerService> logger,
            RabbitMQOptions options,
            IServiceProvider serviceProvider,
            QueueMessageAuthenticator messageAuthenticator)
        {
            _connection = mqService.CreateChannel();
            _logger = logger;
            _riderInfoQueueName = options.RiderInfoQueueName;
            _imageStreamQueueName = options.ImageStreamQueueName;
            _riderInfoPoisonQueueName = options.RiderPoisonStreamQueueName;
            _channel = _connection.CreateModel();
            InitializeQueues();
            _serviceProvider = serviceProvider;
            _messageAuthenticator = messageAuthenticator;
        }

        private void InitializeQueues()
        {
            _channel.QueueDeclare(queue: _riderInfoQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.QueueDeclare(queue: _imageStreamQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
        }

        public async Task StartConsuming()
        {
            ConsumeQueueAsync(_riderInfoQueueName, ProcessRiderInfo);
            ConsumeQueueAsync(_imageStreamQueueName, ProcessImageStream);
            await ConsumePoisonQueue(_riderInfoPoisonQueueName);
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
                catch (QueueMessageAuthenticationException ex)
                {
                    _channel.BasicNack(ea.DeliveryTag, false, false);
                    _logger.LogWarning(ex, "Rejected unauthenticated message from queue {QueueName}.", queueName);
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
            var riderInfo = _messageAuthenticator.ValidateMessage<RiderMQEntity>(
                message,
                "rider.registration.v1",
                payload => payload.UserId);

            using var scope = _serviceProvider.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<RiderInboxMessageHandler>();
            await handler.HandleRegistrationAsync(riderInfo);
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


        private async Task ProcessImageStream(string message)
        {
            var imagePart = _messageAuthenticator.ValidateMessage<ImagePart>(
                message,
                "rider.cnh-image-part.v1",
                payload => payload.UserId);

            using var scope = _serviceProvider.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<RiderInboxMessageHandler>();
            await handler.HandleImagePartAsync(imagePart);
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
