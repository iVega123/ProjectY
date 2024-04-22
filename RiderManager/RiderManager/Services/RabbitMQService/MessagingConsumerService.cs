using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using System.Text;
using RiderManager.Configurations;
using System.Collections.Concurrent;
using RiderManager.Entities;
using System.Text.Json;
using RiderManager.Managers;
using RiderManager.DTOs;

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
        private ConcurrentDictionary<string, List<ImagePart>> imagePartsStore = new ConcurrentDictionary<string, List<ImagePart>>();
        private ConcurrentDictionary<string, int> riderInfoRetryCounts = new ConcurrentDictionary<string, int>();

        public MessagingConsumerService(IRabbitMqService mqService,
            ILogger<MessagingConsumerService> logger, 
            RabbitMQOptions options,
            IServiceProvider serviceProvider)
        {
            _connection = mqService.CreateChannel();
            _logger = logger;
            _riderInfoQueueName = options.RiderInfoQueueName;
            _imageStreamQueueName = options.ImageStreamQueueName;
            _riderInfoPoisonQueueName = options.RiderPoisonStreamQueueName;
            _channel = _connection.CreateModel();
            InitializeQueues();
            _serviceProvider = serviceProvider;
        }

        private void InitializeQueues()
        {
            _channel.QueueDeclare(queue: _riderInfoQueueName, durable: false, exclusive: false);
            _channel.QueueDeclare(queue: _imageStreamQueueName, durable: false, exclusive: false);
            _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
        }

        public async Task StartConsuming()
        {
            ConsumeQueueAsync(_riderInfoQueueName, ProcessRiderInfo);
            ConsumeQueueAsync(_imageStreamQueueName, ProcessImageStream);
            ConsumePoisonQueue(_riderInfoPoisonQueueName);
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
            var riderInfo = JsonSerializer.Deserialize<RiderMQEntity>(message);
            string messageId = riderInfo.UserId;

            if (!riderInfoRetryCounts.TryGetValue(messageId, out int currentRetryCount))
            {
                currentRetryCount = 0;
            }

            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var riderManager = scope.ServiceProvider.GetRequiredService<IRiderManager>();
                    await riderManager.AddRiderAsync(new RiderDTO
                    {
                        UserId = riderInfo.UserId,
                        Email = riderInfo.Email,
                        Name = riderInfo.Name,
                        CNPJ = riderInfo.CNPJ,
                        DateOfBirth = riderInfo.DateOfBirth,
                        CNHNumber = riderInfo.CNHNumber,
                        CNHType = riderInfo.CNHType
                    });
                    riderInfoRetryCounts.TryRemove(messageId, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing rider info: {ex.Message}", ex);
                currentRetryCount++;
                if (currentRetryCount >= 3)
                {
                    _logger.LogError($"Max retry attempts exceeded for message {messageId}. Moving to poison queue.");
                    MoveToPoisonQueue(message, "RiderInfoPoisonQueue");
                    riderInfoRetryCounts.TryRemove(messageId, out _);
                }
                else
                {
                    riderInfoRetryCounts[messageId] = currentRetryCount;
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


        private async Task ProcessImageStream(string message)
        {
            var imagePart = JsonSerializer.Deserialize<ImagePart>(message);
            List<ImagePart> parts = imagePartsStore.GetOrAdd(imagePart.UserId, new List<ImagePart>());
            parts.Add(imagePart);

            if (imagePart.EndOfFile)
            {
                parts.Sort((x, y) => x.SequenceNumber.CompareTo(y.SequenceNumber));

                using (var memoryStream = new MemoryStream())
                {
                    foreach (var part in parts)
                    {
                        memoryStream.Write(part.Content, 0, part.Content.Length);
                    }

                    memoryStream.Position = 0;
                    await StoreImage(memoryStream, imagePart.FileName, imagePart.UserId);
                }

                imagePartsStore.TryRemove(imagePart.UserId, out _);
            }
        }

        private async Task StoreImage(MemoryStream imageStream, string fileName, string userId)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var riderManager = scope.ServiceProvider.GetRequiredService<IRiderManager>();
                var toFormFile = ConvertToIFormFile(imageStream, fileName);
                await riderManager.UpdateRiderImageAsync(userId, toFormFile);
            }
        }

        private IFormFile ConvertToIFormFile(MemoryStream memoryStream, string fileName)
        {
            memoryStream.Position = 0;

            IFormFile formFile = new FormFile(memoryStream, 0, memoryStream.Length, "name", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/octet-stream"
            };

            return formFile;
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
