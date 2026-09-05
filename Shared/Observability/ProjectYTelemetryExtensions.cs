using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace ProjectY.Shared.Observability;

public static class ProjectYTelemetryExtensions
{
    public static IServiceCollection AddProjectYTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string fallbackServiceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        params string[] additionalActivitySources)
    {
        var endpoint = GetEndpoint(configuration);
        if (endpoint is null)
        {
            return services;
        }

        var serviceName = GetServiceName(configuration, fallbackServiceName);
        var protocol = GetProtocol(configuration);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics => metrics.AddMeter("ProjectY.Messaging").AddOtlpExporter(options =>
            {
                options.Endpoint = endpoint;
                options.Protocol = protocol;
            }))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(MessagingTraceContext.ActivitySourceName)
                    .AddSource(additionalActivitySources)
                    .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = endpoint;
                        options.Protocol = protocol;
                    });
                configureTracing?.Invoke(tracing);
            });

        return services;
    }

    public static LoggerConfiguration WriteToProjectYTelemetry(
        this LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        string fallbackServiceName)
    {
        var endpoint = GetEndpoint(configuration);
        if (endpoint is null)
        {
            return loggerConfiguration;
        }

        var serviceName = GetServiceName(configuration, fallbackServiceName);
        var protocol = GetProtocol(configuration) == OtlpExportProtocol.Grpc
            ? OtlpProtocol.Grpc
            : OtlpProtocol.HttpProtobuf;

        return loggerConfiguration.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = endpoint.ToString();
            options.Protocol = protocol;
            options.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = serviceName
            };
            options.OnBeginSuppressInstrumentation = SuppressInstrumentationScope.Begin;
        });
    }

    private static Uri? GetEndpoint(IConfiguration configuration)
    {
        var rawEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            return null;
        }

        if (!Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                "OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute URI.");
        }

        return endpoint;
    }

    private static string GetServiceName(
        IConfiguration configuration,
        string fallbackServiceName) =>
        configuration["OTEL_SERVICE_NAME"]
        ?? configuration["ApplicationName"]
        ?? fallbackServiceName;

    private static OtlpExportProtocol GetProtocol(IConfiguration configuration) =>
        configuration["OTEL_EXPORTER_OTLP_PROTOCOL"]?.ToLowerInvariant() switch
        {
            null or "grpc" => OtlpExportProtocol.Grpc,
            "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
            var unsupported => throw new InvalidOperationException(
                $"Unsupported OTEL_EXPORTER_OTLP_PROTOCOL '{unsupported}'.")
        };
}
