using System.Diagnostics;
using System.Text;

namespace ProjectY.Shared.Observability;

public static class MessagingTraceContext
{
    public const string ActivitySourceName = "ProjectY.Messaging";
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    public static (string? TraceParent, string? TraceState) CaptureCurrent()
        => (Activity.Current?.Id, Activity.Current?.TraceStateString);

    public static Activity? StartProducerActivity(
        string messagingSystem,
        string destination,
        string? traceParent,
        string? traceState)
        => StartActivity(
            $"{destination} publish",
            ActivityKind.Producer,
            messagingSystem,
            destination,
            "publish",
            traceParent,
            traceState);

    public static Activity? StartConsumerActivity(
        string messagingSystem,
        string destination,
        IDictionary<string, object>? headers,
        string? messageId = null)
    {
        var activity = StartActivity(
            $"{destination} process",
            ActivityKind.Consumer,
            messagingSystem,
            destination,
            "process",
            ReadHeader(headers, TraceParentHeader),
            ReadHeader(headers, TraceStateHeader));
        activity?.SetTag("messaging.message.id", messageId);
        return activity;
    }

    public static void InjectCurrent(
        IDictionary<string, object> headers,
        string? fallbackTraceParent = null,
        string? fallbackTraceState = null)
    {
        var traceParent = Activity.Current?.Id ?? fallbackTraceParent;
        var traceState = Activity.Current?.TraceStateString ?? fallbackTraceState;

        SetHeader(headers, TraceParentHeader, traceParent);
        SetHeader(headers, TraceStateHeader, traceState);
    }

    public static void RecordException(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetTag("exception.message", exception.Message);
    }

    private static Activity? StartActivity(
        string name,
        ActivityKind kind,
        string messagingSystem,
        string destination,
        string operation,
        string? traceParent,
        string? traceState)
    {
        var parent = TryParseParent(traceParent, traceState, out var parsed)
            ? parsed
            : default;
        var tags = new ActivityTagsCollection
        {
            ["messaging.system"] = messagingSystem,
            ["messaging.destination.name"] = destination,
            ["messaging.operation.name"] = operation,
            ["messaging.operation.type"] = kind == ActivityKind.Producer ? "send" : "process"
        };

        return Source.StartActivity(name, kind, parent, tags);
    }

    private static bool TryParseParent(
        string? traceParent,
        string? traceState,
        out ActivityContext parent)
        => ActivityContext.TryParse(traceParent, traceState, isRemote: true, out parent);

    private static string? ReadHeader(IDictionary<string, object>? headers, string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var value))
        {
            return null;
        }

        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> bytes => Encoding.UTF8.GetString(bytes.Span),
            string text => text,
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static void SetHeader(
        IDictionary<string, object> headers,
        string name,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            headers.Remove(name);
            return;
        }

        headers[name] = Encoding.UTF8.GetBytes(value);
    }
}
