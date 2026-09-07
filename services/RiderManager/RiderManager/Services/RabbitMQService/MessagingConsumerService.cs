using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using System.Text;
using RiderManager.Configurations;
using RiderManager.Entities;
using ProjectY.Shared.Messaging;
using ProjectY.Shared.Observability;

namespace RiderManager.Services.RabbitMQService
{
    public class MessagingConsumerService : IMessagingConsumerService, IDisposable
    {
        private readonly IModel _channel;
        private readonly IModel _retryChannel;
        private readonly ILogger<MessagingConsumerService> _logger;
        private readonly IConnection _connection;
        private readonly string _riderInfoQueueName;
        private readonly string _imageStreamQueueName;
        private readonly string _riderInfoPoisonQueueName;
        private readonly IServiceProvider _serviceProvider;
        private readonly QueueMessageAuthenticator _messageAuthenticator;
        private readonly BoundedRabbitMqRetryRouter _retryRouter;

        public MessagingConsumerService(IRabbitMqService mqService,
            ILogger<MessagingConsumerService> logger,
            RabbitMQOptions options,
            IServiceProvider serviceProvider,
            QueueMessageAuthenticator messageAuthenticator,
            BoundedRabbitMqRetryRouter retryRouter)
        {
            _connection = mqService.CreateChannel();
            _logger = logger;
            _riderInfoQueueName = options.RiderInfoQueueName;
            _imageStreamQueueName = options.ImageStreamQueueName;
            _riderInfoPoisonQueueName = options.RiderPoisonStreamQueueName;
            _channel = _connection.CreateModel();
            _retryChannel = _connection.CreateModel();
            _retryRouter = retryRouter;
            InitializeQueues();
            _serviceProvider = serviceProvider;
            _messageAuthenticator = messageAuthenticator;
        }

        private void InitializeQueues()
        {
            _channel.QueueDeclare(queue: _riderInfoQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.QueueDeclare(queue: _imageStreamQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.QueueDeclare(queue: _riderInfoPoisonQueueName, durable: true, exclusive: false, autoDelete: false);
            _retryRouter.DeclareTopology(_retryChannel, _riderInfoQueueName, _riderInfoPoisonQueueName);
            _retryRouter.DeclareTopology(_retryChannel, _imageStreamQueueName, _riderInfoPoisonQueueName);
            _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
        }

        public Task StartConsuming()
        {
            ConsumeQueueAsync(_riderInfoQueueName, ProcessRiderInfo, _riderInfoPoisonQueueName);
            ConsumeQueueAsync(_imageStreamQueueName, ProcessImageStream, _riderInfoPoisonQueueName);
            return Task.CompletedTask;
        }

        private void ConsumeQueueAsync(
            string queueName,
            Func<string, Task> processMessageFunc,
            string poisonQueueName)
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
                    await processMessageFunc(message);
                    _channel.BasicAck(ea.DeliveryTag, false);
                }
                catch (Exception ex)
                {
                    MessagingTraceContext.RecordException(activity, ex);
                    try
                    {
                        var route = _retryRouter.RouteFailure(
                            _retryChannel,
                            queueName,
                            poisonQueueName,
                            ea.BasicProperties,
                            ea.Body, permanent: ex is QueueMessageAuthenticationException or InvalidDataException);
                        _channel.BasicAck(ea.DeliveryTag, false);
                        if (route == FailureRoute.Retry)
                        {
                            _logger.LogWarning(
                                ex,
                                "Message processing failed; delivery was republished for a bounded retry.");
                        }
                        else
                        {
                            _logger.LogWarning(
                                ex,
                                "Message processing failed permanently; delivery was quarantined in {PoisonQueueName}.",
                                poisonQueueName);
                        }
                    }
                    catch (Exception routingException)
                    {
                        _channel.Abort();
                        _serviceProvider.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                        _logger.LogError(
                            routingException,
                            "Could not route failed message; consumer stopped with original delivery unacknowledged.");
                    }
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

        public void Dispose()
        {
            _channel?.Close();
            _channel?.Dispose();
            _retryChannel?.Close();
            _retryChannel?.Dispose();
            _connection.Dispose();
            _logger.LogInformation("RabbitMQ channel closed.");
        }
    }
}
