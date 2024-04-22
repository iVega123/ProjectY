using MotoHub.Configurations;
using MotoHub.Entities;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace MotoHub.Services.RabbitMQ
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

        public void PublishLicenceUpdate(LicencePlateRabbitMQEntity licenceUpdate)
        {
            _channel.QueueDeclare(_rabbitmqOptions.LicenceUpdateQueueName, false, false);

            var message = JsonSerializer.Serialize(licenceUpdate);
            var body = Encoding.UTF8.GetBytes(message);

            _channel.BasicPublish(exchange: "",
                                  routingKey: _rabbitmqOptions.LicenceUpdateQueueName,
                                  basicProperties: null,
                                  body: body);
        }

    }
}
