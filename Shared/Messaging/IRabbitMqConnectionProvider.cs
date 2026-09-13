using RabbitMQ.Client;

namespace ProjectY.Shared.Messaging;

public interface IRabbitMqConnectionProvider
{
    Task<IConnection> CreateAsync(CancellationToken cancellationToken);
}

public sealed class RabbitMqConnectionProvider : IRabbitMqConnectionProvider
{
    private readonly OutboxRelayOptions _options;

    public RabbitMqConnectionProvider(OutboxRelayOptions options)
    {
        _options = options;
    }

    public Task<IConnection> CreateAsync(CancellationToken cancellationToken) => new ConnectionFactory
    {
        HostName = _options.HostName,
        Port = _options.Port,
        VirtualHost = _options.VirtualHost,
        UserName = _options.UserName,
        Password = _options.Password,
        AutomaticRecoveryEnabled = true
    }.CreateConnectionAsync(cancellationToken);
}
