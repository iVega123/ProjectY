using AuthGate.Configurations;
using AuthGate.Entities;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace AuthGate.Services.RabbitMQ
{
    public class MessagingPublisherService : IMessagingPublisherService
    {
        private readonly IConnection _connection;
        private readonly IModel _channel;
        private readonly RabbitMQOptions _rabbitmqOptions;

        public MessagingPublisherService(IConnection connection, RabbitMQOptions rabbitMQOptions)
        {
            _connection = connection;
            _channel = _connection.CreateModel();
            _rabbitmqOptions = rabbitMQOptions;
        }

        public void PublishImageStream(Stream imageStream, string extension, string userId)
        {
            const int bufferSize = 4096;
            byte[] buffer = new byte[bufferSize];
            int byteCount;
            int sequenceNumber = 0;

            _channel.QueueDeclare(_rabbitmqOptions.ImageStreamQueueName, false, false);

            string fileName = $"{userId}_{DateTime.UtcNow:yyyyMMddHHmmss}{extension}";

            while ((byteCount = imageStream.Read(buffer, 0, bufferSize)) > 0)
            {
                var message = new
                {
                    UserId = userId,
                    SequenceNumber = sequenceNumber++,
                    FileName = fileName,
                    Content = Convert.ToBase64String(buffer, 0, byteCount),
                    EndOfFile = imageStream.Position == imageStream.Length
                };

                var json = JsonSerializer.Serialize(message);
                var body = Encoding.UTF8.GetBytes(json);

                _channel.BasicPublish(exchange: "",
                                      routingKey: _rabbitmqOptions.ImageStreamQueueName,
                                      basicProperties: null,
                                      body: body);
            }
        }

        public void PublishRiderInfo(RiderMQEntity rider)
        {
            _channel.QueueDeclare(_rabbitmqOptions.RiderInfoQueueName, false, false);

            var message = JsonSerializer.Serialize(rider);
            var body = Encoding.UTF8.GetBytes(message);

            _channel.BasicPublish(exchange: "",
                                  routingKey: _rabbitmqOptions.RiderInfoQueueName,
                                  basicProperties: null,
                                  body: body);
        }

    }
}
