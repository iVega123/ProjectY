using RabbitMQ.Client;

namespace RiderManager.Services.RabbitMQService;

public enum FailureRoute
{
    Retry,
    Poison
}

public sealed class BoundedRabbitMqRetryRouter
{
    public const int MaximumRetryCount = 3;
    public const string RetryHeader = "x-retries";
    public static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(5);

    public FailureRoute RouteFailure(
        IModel channel,
        string sourceQueue,
        string poisonQueue,
        IBasicProperties originalProperties,
        ReadOnlyMemory<byte> body)
    {
        var retryCount = ReadRetryCount(originalProperties.Headers);
        var route = retryCount < MaximumRetryCount
            ? FailureRoute.Retry
            : FailureRoute.Poison;
        var destination = route == FailureRoute.Retry ? sourceQueue : poisonQueue;

        channel.QueueDeclare(destination, durable: true, exclusive: false, autoDelete: false);
        channel.ConfirmSelect();
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.MessageId = originalProperties.MessageId;
        properties.CorrelationId = originalProperties.CorrelationId;
        properties.Type = originalProperties.Type;
        properties.ContentType = originalProperties.ContentType;
        properties.ContentEncoding = originalProperties.ContentEncoding;
        properties.Headers = originalProperties.Headers is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(originalProperties.Headers);
        properties.Headers[RetryHeader] = route == FailureRoute.Retry
            ? retryCount + 1
            : retryCount;

        channel.BasicPublish(
            exchange: string.Empty,
            routingKey: destination,
            mandatory: true,
            basicProperties: properties,
            body: body);
        channel.WaitForConfirmsOrDie(ConfirmationTimeout);

        return route;
    }

    private static int ReadRetryCount(IDictionary<string, object>? headers)
    {
        if (headers is null || !headers.TryGetValue(RetryHeader, out var value))
        {
            return 0;
        }

        try
        {
            return Math.Max(0, Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }
}
