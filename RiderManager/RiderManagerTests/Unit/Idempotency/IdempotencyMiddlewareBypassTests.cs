using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectY.Shared.Idempotency;
using StackExchange.Redis;

namespace RiderManagerTests.Unit.Idempotency;

public sealed class IdempotencyMiddlewareBypassTests
{
    [Fact]
    public async Task StateChangingRequestWithoutKey_DoesNotResolveRedis()
    {
        var executed = false;
        var middleware = CreateWithoutRedis(_ =>
        {
            executed = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;

        await middleware.InvokeAsync(context);

        Assert.True(executed);
    }

    [Fact]
    public async Task SafeRequestWithKey_DoesNotResolveRedis()
    {
        var executed = false;
        var middleware = CreateWithoutRedis(_ =>
        {
            executed = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers[IdempotencyOptions.HeaderName] = "ignored-on-safe-method";

        await middleware.InvokeAsync(context);

        Assert.True(executed);
    }

    private static RedisIdempotencyMiddleware CreateWithoutRedis(RequestDelegate next)
        => new(
            next,
            new Lazy<IConnectionMultiplexer>(() =>
                throw new InvalidOperationException("Redis should not be resolved.")),
            new IdempotencyOptions(),
            NullLogger<RedisIdempotencyMiddleware>.Instance);
}
