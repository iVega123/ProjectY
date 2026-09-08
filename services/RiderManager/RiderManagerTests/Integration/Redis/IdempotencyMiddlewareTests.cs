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
    [Trait("Guarantee", "ADR-0009#http-idempotency")]
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
    [Trait("Guarantee", "ADR-0009#http-idempotency")]
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
    [Trait("Guarantee", "ADR-0009#http-idempotency")]
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
    [Trait("Guarantee", "ADR-0009#http-idempotency")]
    public async Task LongRunningRequest_RetainsClaimUntilItCompletes()
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

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#http-idempotency")]
    public async Task DownstreamFailure_RetainsUnknownOutcomeWithoutRepeatingEffect()
    {
        var effects = 0;
        var middleware = CreateMiddleware(_ =>
        {
            Interlocked.Increment(ref effects);
            throw new InvalidOperationException("Failure after a possible commit.");
        });
        var key = Guid.NewGuid().ToString("D");

        var first = await ExecuteAsync(middleware, key, "{\"plate\":\"IDEM-0005\"}");
        var replay = await ExecuteAsync(middleware, key, "{\"plate\":\"IDEM-0005\"}");

        Assert.Equal(1, effects);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, replay.Response.StatusCode);
        Assert.Equal("true", replay.Response.Headers["Idempotency-Replayed"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#http-idempotency")]
    public async Task ReusingKeyWithReorderedQueryValues_ReturnsUnprocessableEntity()
    {
        var effects = 0;
        var middleware = CreateMiddleware(context =>
        {
            Interlocked.Increment(ref effects);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        var key = Guid.NewGuid().ToString("D");

        await ExecuteAsync(
            middleware,
            key,
            string.Empty,
            "?rentalId=A&rentalId=B");
        var mismatch = await ExecuteAsync(
            middleware,
            key,
            string.Empty,
            "?rentalId=B&rentalId=A");

        Assert.Equal(1, effects);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, mismatch.Response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Integration")]
    public async Task Dependency503_OnlyReleasesExplicitPreWriteFailures(bool beforeWrite)
    {
        var attempts = 0;
        var effects = 0;
        var middleware = CreateMiddleware(async context =>
        {
            attempts++;
            if (attempts == 1)
            {
                if (beforeWrite) RedisIdempotencyMiddleware.AllowRetryBeforeSideEffects(context);
                else effects++;
                context.Response.StatusCode = 503;
                context.Response.Headers.RetryAfter = "1";
                await context.Response.WriteAsync("dependency unavailable");
                return;
            }
            effects++;
            context.Response.StatusCode = 201;
            await context.Response.WriteAsync("created");
        });
        var key = Guid.NewGuid().ToString("N");
        var first = await ExecuteAsync(middleware, key, "{}");
        var retry = await ExecuteAsync(middleware, key, "{}");
        var replay = await ExecuteAsync(middleware, key, "{}");
        Assert.Equal(503, first.Response.StatusCode);
        Assert.Equal("1", first.Response.Headers.RetryAfter.ToString());
        Assert.Equal(beforeWrite ? 201 : 503, retry.Response.StatusCode);
        Assert.Equal(beforeWrite ? 2 : 1, attempts);
        Assert.Equal(1, effects);
        Assert.Equal("true", replay.Response.Headers["Idempotency-Replayed"].ToString());
    }

    private RedisIdempotencyMiddleware CreateMiddleware(RequestDelegate next)
        => new(
            next,
            new Lazy<IConnectionMultiplexer>(() => _connection),
            new IdempotencyOptions
            {
                ServiceName = "idempotency-tests",
                ResponseTtl = TimeSpan.FromHours(1)
            },
            NullLogger<RedisIdempotencyMiddleware>.Instance);

    private static async Task<HttpContext> ExecuteAsync(
        RedisIdempotencyMiddleware middleware,
        string key,
        string body,
        string? query = null)
    {
        var context = CreateContext(key, body, query);
        await middleware.InvokeAsync(context);
        return context;
    }

    private static HttpContext CreateContext(string key, string body, string? query = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/rentals";
        context.Request.QueryString = new QueryString(query ?? string.Empty);
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
