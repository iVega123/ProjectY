using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProjectY.Shared.Idempotency;
using StackExchange.Redis;

namespace RentalOperationsTests.Unit;

/// <summary>
/// The closed half of the Redis row.
///
/// Without Redis there is no record that this key has not already run. Running
/// the mutation anyway would turn a client retry after a lost response into a
/// second rental, so the request is refused before it reaches the handler. A
/// middleware that "degraded open" here would pass the first assertion's opposite.
/// </summary>
public sealed class IdempotencyFailClosedTests
{
    [Fact]
    [Trait("Degradation", "redis")]
    public async Task RedisUnavailable_RefusesTheMutation_WithoutRunningIt()
    {
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer
            .Setup(redis => redis.GetDatabase(It.IsAny<int>(), It.IsAny<object?>()))
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis unavailable"));
        var handlerRan = false;
        var middleware = new RedisIdempotencyMiddleware(
            _ => { handlerRan = true; return Task.CompletedTask; },
            new Lazy<IConnectionMultiplexer>(() => multiplexer.Object),
            new IdempotencyOptions { ServiceName = "rental-core" },
            NullLogger<RedisIdempotencyMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/Rental/create";
        context.Request.Headers[IdempotencyOptions.HeaderName] = "retry-after-lost-response";
        context.Request.Body = new MemoryStream("{\"motorcycleId\":\"m\"}"u8.ToArray());
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(handlerRan);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }
}
