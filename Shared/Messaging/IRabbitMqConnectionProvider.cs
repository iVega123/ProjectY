using RabbitMQ.Client;

namespace ProjectY.Shared.Messaging;

public interface IRabbitMqConnectionProvider
{
    IConnection Create();
}

public sealed class RabbitMqConnectionProvider : IRabbitMqConnectionProvider
{
    private readonly OutboxRelayOptions _options;

    public RabbitMqConnectionProvider(OutboxRelayOptions options)
    {
        _options = options;
    }

    public IConnection Create() => new ConnectionFactory
    {
        HostName = _options.HostName,
        Port = _options.Port,
        VirtualHost = _options.VirtualHost,
        UserName = _options.UserName,
        Password = _options.Password,
        AutomaticRecoveryEnabled = true
    }.CreateConnection();
}
