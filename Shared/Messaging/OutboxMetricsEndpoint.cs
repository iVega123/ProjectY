using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace ProjectY.Shared.Messaging;

public static class OutboxMetricsEndpoint
{
    public static IEndpointConventionBuilder MapOutboxMetrics<TContext>(
        this IEndpointRouteBuilder endpoints,
        string serviceName)
        where TContext : DbContext
    {
        return endpoints.MapGet("/metrics", async (IServiceProvider services, CancellationToken cancellationToken) =>
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var pending = context.Set<OutboxMessage>().Where(message => message.PublishedAtUtc == null);
            var depth = await pending.LongCountAsync(cancellationToken);
            var oldest = await pending.MinAsync(message => (DateTime?)message.OccurredAtUtc, cancellationToken);
            var lag = oldest is null ? 0 : Math.Max(0, (DateTime.UtcNow - oldest.Value).TotalSeconds);
            var service = EscapeLabel(serviceName);
            var payload = string.Create(
                CultureInfo.InvariantCulture,
                $"# TYPE projecty_outbox_depth gauge\nprojecty_outbox_depth{{service=\"{service}\"}} {depth}\n" +
                $"# TYPE projecty_outbox_oldest_age_seconds gauge\nprojecty_outbox_oldest_age_seconds{{service=\"{service}\"}} {lag:F3}\n");

            return Results.Text(payload, "text/plain; version=0.0.4; charset=utf-8");
        }).AllowAnonymous();
    }

    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
