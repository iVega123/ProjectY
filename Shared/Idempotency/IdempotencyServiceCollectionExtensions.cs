using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace ProjectY.Shared.Idempotency;

public static class IdempotencyServiceCollectionExtensions
{
    public static IServiceCollection AddProjectYIdempotency(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        var options = configuration.GetSection("Idempotency").Get<IdempotencyOptions>()
            ?? new IdempotencyOptions();
        options.ServiceName = serviceName;
        options.RedisConnectionString = configuration["Redis:ConnectionString"]
            ?? options.RedisConnectionString;
        if (options.ClaimTtl <= TimeSpan.Zero || options.ResponseTtl <= options.ClaimTtl)
        {
            throw new InvalidOperationException(
                "Idempotency:ClaimTtl must be positive and shorter than Idempotency:ResponseTtl.");
        }

        if (options.MaximumKeyLength <= 0)
        {
            throw new InvalidOperationException("Idempotency:MaximumKeyLength must be positive.");
        }

        services.AddSingleton(options);
        services.AddSingleton(_ => new Lazy<IConnectionMultiplexer>(() =>
        {
            var redisOptions = ConfigurationOptions.Parse(options.RedisConnectionString);
            redisOptions.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(redisOptions);
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        return services;
    }

    public static IApplicationBuilder UseProjectYIdempotency(this IApplicationBuilder app)
        => app.UseMiddleware<RedisIdempotencyMiddleware>();
}
