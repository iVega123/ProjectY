using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace ProjectY.Shared.Messaging;

public enum FailureRoute { Retry, Poison }

// Retry state travels with the durable message, shared by every replica.
public sealed class BoundedRabbitMqRetryRouter
{
    public const int MaximumRetryCount = 3;
    public const string RetryHeader = "x-retries";
    public static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(5);
    private static readonly Meter Meter = new("ProjectY.Messaging");
    private static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>("messaging.dead_letters");

    public void DeclareTopology(IModel channel, string sourceQueue, string poisonQueue)
    {
        channel.QueueDeclare(sourceQueue, true, false, false);
        channel.QueueDeclare(poisonQueue, true, false, false);
        channel.ExchangeDeclare(sourceQueue + ".redelivery", ExchangeType.Direct, true);
        channel.QueueBind(sourceQueue, sourceQueue + ".redelivery", sourceQueue);
        channel.ExchangeDeclare(sourceQueue + ".dead", ExchangeType.Direct, true);
        channel.QueueBind(poisonQueue, sourceQueue + ".dead", sourceQueue);
        for (var attempt = 1; attempt <= MaximumRetryCount; attempt++)
        {
            channel.QueueDeclare($"{sourceQueue}.retry.{attempt}", true, false, false,
                new Dictionary<string, object>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-message-ttl"] = 1000 * (1 << attempt),
                    ["x-dead-letter-exchange"] = sourceQueue + ".redelivery",
                    ["x-dead-letter-routing-key"] = sourceQueue,
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish",
                    ["x-max-length"] = 10000
                });
        }
    }

    public FailureRoute RouteFailure(IModel channel, string sourceQueue, string poisonQueue,
        IBasicProperties originalProperties, ReadOnlyMemory<byte> body, bool permanent = false)
    {
        var count = ReadRetryCount(originalProperties.Headers);
        var route = !permanent && count < MaximumRetryCount ? FailureRoute.Retry : FailureRoute.Poison;
        DeclareTopology(channel, sourceQueue, poisonQueue);
        channel.ConfirmSelect();
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.MessageId = originalProperties.MessageId;
        properties.CorrelationId = originalProperties.CorrelationId;
        properties.Type = originalProperties.Type;
        properties.ContentType = originalProperties.ContentType;
        properties.ContentEncoding = originalProperties.ContentEncoding;
        properties.Headers = originalProperties.Headers is null
            ? new Dictionary<string, object>() : new Dictionary<string, object>(originalProperties.Headers);
        properties.Headers[RetryHeader] = route == FailureRoute.Retry ? count + 1 : count;
        // This channel is exclusively owned by the router. A mandatory return
        // must be checked as well as the broker's publisher confirmation.
        BasicReturnEventArgs? returned = null;
        void OnReturn(object? sender, BasicReturnEventArgs args) => returned = args;
        channel.BasicReturn += OnReturn;
        try
        {
            channel.BasicPublish(route == FailureRoute.Retry ? "" : sourceQueue + ".dead",
                route == FailureRoute.Retry ? $"{sourceQueue}.retry.{count + 1}" : sourceQueue,
                true, properties, body);
            channel.WaitForConfirmsOrDie(ConfirmationTimeout);
            if (returned is not null) { throw new IOException("Retry/DLQ publication was unroutable."); }
        }
        finally { channel.BasicReturn -= OnReturn; }
        if (route == FailureRoute.Poison)
        {
            DeadLetters.Add(1, new KeyValuePair<string, object?>("queue", sourceQueue));
        }
        return route;
    }

    private static int ReadRetryCount(IDictionary<string, object>? headers)
    {
        if (headers is null || !headers.TryGetValue(RetryHeader, out var value)) { return 0; }
        try { return Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, MaximumRetryCount); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        { return MaximumRetryCount; }
    }
}
