namespace ProjectY.Shared.Messaging;

public sealed class OutboxRelayOptions
{
    public required string ServiceName { get; init; }
    public required string HostName { get; init; }
    public int Port { get; init; } = 5672;
    public required string VirtualHost { get; init; }
    public required string UserName { get; init; }
    public required string Password { get; init; }
    public int BatchSize { get; init; } = 100;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ClaimLeaseDuration { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan ConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
