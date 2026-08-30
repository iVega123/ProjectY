using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace ProjectY.Shared.Health;

public static class ServiceHealthChecks
{
    private static readonly string[] ReadyTag = ["ready"];
    private static readonly string[] StartupTag = ["startup"];

    public static IHealthChecksBuilder AddProjectYHealthChecks(this IServiceCollection services)
    {
        services.AddSingleton<StartupHealthCheck>();

        return services
            .AddHealthChecks()
            .AddCheck<StartupHealthCheck>(
                "startup",
                failureStatus: HealthStatus.Unhealthy,
                tags: StartupTag);
    }

    public static IHealthChecksBuilder AddTcpDependency(
        this IHealthChecksBuilder builder,
        string name,
        string host,
        int port)
    {
        return builder.AddCheck(
            name,
            new TcpDependencyHealthCheck(host, port),
            failureStatus: HealthStatus.Unhealthy,
            tags: ReadyTag);
    }

    public static IEndpointRouteBuilder MapProjectYHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready")
        });
        endpoints.MapHealthChecks("/health/startup", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("startup")
        });

        return endpoints;
    }
}

public sealed class StartupHealthCheck(IHostApplicationLifetime applicationLifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var result = applicationLifetime.ApplicationStarted.IsCancellationRequested
            ? HealthCheckResult.Healthy("Application startup completed.")
            : HealthCheckResult.Unhealthy("Application startup is still in progress.");

        return Task.FromResult(result);
    }
}

public sealed class TcpDependencyHealthCheck(string host, int port) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var client = new TcpClient();
            await client
                .ConnectAsync(host, port, timeout.Token)
                .AsTask()
                .WaitAsync(Timeout, cancellationToken);
            return HealthCheckResult.Healthy($"{host}:{port} is reachable.");
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy($"{host}:{port} is unreachable.", exception);
        }
    }
}

public static class HealthProbeCommand
{
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--healthcheck", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(args[1]);
            Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
        }

        return true;
    }
}
