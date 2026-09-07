using ProjectY.Shared.Messaging;
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
        private readonly IModel _retryChannel;
        private readonly BoundedRabbitMqRetryRouter _retryRouter = new();
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
            _retryChannel = _connection.CreateModel();
            InitializeQueues();
            _serviceProvider = serviceProvider;
        }

        private void InitializeQueues()
        {
            _channel.QueueDeclare(queue: _licenceUpdateQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.QueueDeclare(queue: _licenceUpdatePoisonQueueName, durable: true, exclusive: false, autoDelete: false);
            _retryRouter.DeclareTopology(_retryChannel, _licenceUpdateQueueName, _licenceUpdatePoisonQueueName);
            _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
        }

        public Task StartConsuming()
        {
            ConsumeQueueAsync(_licenceUpdateQueueName, ProcessLicenceUpdate);
            return Task.CompletedTask;
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
                    try
                    {
                        _retryRouter.RouteFailure(_retryChannel, queueName, _licenceUpdatePoisonQueueName,
                            ea.BasicProperties, ea.Body, permanent: ex is JsonException);
                        _channel.BasicAck(ea.DeliveryTag, false);
                    }
                    catch (Exception routingException)
                    {
                        _logger.LogCritical(routingException, "Retry routing failed; stopping with delivery unacknowledged.");
                        _channel.Abort();
                        _serviceProvider.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                    }
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

        private static string GetMessageId(IBasicProperties properties, byte[] body)
            => string.IsNullOrWhiteSpace(properties.MessageId)
                ? Convert.ToHexString(SHA256.HashData(body))
                : properties.MessageId;

        public void Dispose()
        {
            _channel?.Close();
            _channel?.Dispose();
            _retryChannel.Dispose();
            _connection.Dispose();
            _logger.LogInformation("RabbitMQ channel closed.");
        }
    }
}
