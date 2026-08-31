using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectY.Shared.Idempotency;
using StackExchange.Redis;
using System.Text;
using Testcontainers.Redis;

namespace RiderManagerTests.Integration.Redis;

public sealed class IdempotencyMiddlewareTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();
    private IConnectionMultiplexer _connection = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReplayingCreateWithSameKey_ReturnsOriginalResponseAndOneEffect()
    {
        var effects = 0;
        var middleware = CreateMiddleware(async context =>
        {
            Interlocked.Increment(ref effects);
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.Headers.Location = "/api/rentals/rental-1";
            await context.Response.WriteAsync("created-rental-1");
        });
        var key = Guid.NewGuid().ToString("D");

        var first = await ExecuteAsync(middleware, key, "{\"plate\":\"IDEM-0001\"}");
        var replay = await ExecuteAsync(middleware, key, "{\"plate\":\"IDEM-0001\"}");

        Assert.Equal(1, effects);
        Assert.Equal(StatusCodes.Status201Created, first.Response.StatusCode);
        Assert.Equal(first.Response.StatusCode, replay.Response.StatusCode);
        Assert.Equal(await ReadBodyAsync(first), await ReadBodyAsync(replay));
        Assert.Equal("/api/rentals/rental-1", replay.Response.Headers.Location);
        Assert.Equal("true", replay.Response.Headers["Idempotency-Replayed"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReusingKeyWithDifferentBody_ReturnsUnprocessableEntity()
    {
        var effects = 0;
        var middleware = CreateMiddleware(context =>
        {
            Interlocked.Increment(ref effects);
            context.Response.StatusCode = StatusCodes.Status201Created;
            return Task.CompletedTask;
        });
        var key = Guid.NewGuid().ToString("D");

        await ExecuteAsync(middleware, key, "{\"plate\":\"IDEM-0002\"}");
        var mismatch = await ExecuteAsync(middleware, key, "{\"plate\":\"DIFFERENT\"}");

        Assert.Equal(1, effects);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, mismatch.Response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentRequestWithSameKey_ReturnsConflictUntilFirstCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = 0;
        var middleware = CreateMiddleware(async context =>
        {
            Interlocked.Increment(ref effects);
            started.TrySetResult();
            await release.Task;
            context.Response.StatusCode = StatusCodes.Status201Created;
        });
        var key = Guid.NewGuid().ToString("D");
        var firstContext = CreateContext(key, "{\"plate\":\"IDEM-0003\"}");

        var first = middleware.InvokeAsync(firstContext);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = await ExecuteAsync(middleware, key, "{\"plate\":\"IDEM-0003\"}");
        release.TrySetResult();
        await first;

        Assert.Equal(1, effects);
        Assert.Equal(StatusCodes.Status409Conflict, concurrent.Response.StatusCode);
        Assert.Equal("1", concurrent.Response.Headers.RetryAfter);
        Assert.Equal(StatusCodes.Status201Created, firstContext.Response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LongRunningRequest_RenewsClaimUntilItCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = 0;
        var middleware = CreateMiddleware(async context =>
        {
            Interlocked.Increment(ref effects);
            started.TrySetResult();
            await release.Task;
            context.Response.StatusCode = StatusCodes.Status201Created;
        }, TimeSpan.FromMilliseconds(300));
        var key = Guid.NewGuid().ToString("D");
        var firstContext = CreateContext(key, "{\"plate\":\"IDEM-0004\"}");

        var first = middleware.InvokeAsync(firstContext);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        var concurrent = await ExecuteAsync(middleware, key, "{\"plate\":\"IDEM-0004\"}");
        release.TrySetResult();
        await first;

        Assert.Equal(1, effects);
        Assert.Equal(StatusCodes.Status409Conflict, concurrent.Response.StatusCode);
        Assert.Equal(StatusCodes.Status201Created, firstContext.Response.StatusCode);
    }

    private RedisIdempotencyMiddleware CreateMiddleware(
        RequestDelegate next,
        TimeSpan? claimTtl = null)
        => new(
            next,
            new Lazy<IConnectionMultiplexer>(() => _connection),
            new IdempotencyOptions
            {
                ServiceName = "idempotency-tests",
                ClaimTtl = claimTtl ?? TimeSpan.FromMinutes(1),
                ResponseTtl = TimeSpan.FromHours(1)
            },
            NullLogger<RedisIdempotencyMiddleware>.Instance);

    private static async Task<HttpContext> ExecuteAsync(
        RedisIdempotencyMiddleware middleware,
        string key,
        string body)
    {
        var context = CreateContext(key, body);
        await middleware.InvokeAsync(context);
        return context;
    }

    private static HttpContext CreateContext(string key, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/rentals";
        context.Request.ContentType = "application/json";
        context.Request.Headers[IdempotencyOptions.HeaderName] = key;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
