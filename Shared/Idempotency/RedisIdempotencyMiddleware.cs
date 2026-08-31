using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using StackExchange.Redis;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectY.Shared.Idempotency;

public sealed class RedisIdempotencyMiddleware
{
    private const string PendingState = "pending";
    private const string CompletedState = "completed";
    private const string ReplayHeader = "Idempotency-Replayed";
    private const string CompleteScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
            return 1
        end
        return 0
        """;
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;
    private const string ExtendScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """;

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    private readonly RequestDelegate _next;
    private readonly Lazy<IConnectionMultiplexer> _redis;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<RedisIdempotencyMiddleware> _logger;

    public RedisIdempotencyMiddleware(
        RequestDelegate next,
        Lazy<IConnectionMultiplexer> redis,
        IdempotencyOptions options,
        ILogger<RedisIdempotencyMiddleware> logger)
    {
        _next = next;
        _redis = redis;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsStateChanging(context.Request.Method)
            || !context.Request.Headers.TryGetValue(IdempotencyOptions.HeaderName, out var header))
        {
            await _next(context);
            return;
        }

        if (!TryReadKey(header, out var idempotencyKey))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                $"{IdempotencyOptions.HeaderName} must contain one non-empty value with at most {_options.MaximumKeyLength} characters.");
            return;
        }

        string fingerprint;
        try
        {
            fingerprint = await CreateFingerprintAsync(context);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Could not read request body for idempotency fingerprinting.");
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Unreadable request body",
                "The request body could not be fingerprinted.");
            return;
        }

        var caller = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var redisKey = CreateRedisKey(idempotencyKey, caller);
        var pending = new IdempotencyRecord
        {
            State = PendingState,
            Fingerprint = fingerprint,
            OwnerToken = Guid.NewGuid().ToString("D")
        };
        var pendingJson = JsonSerializer.Serialize(pending);

        try
        {
            var database = _redis.Value.GetDatabase();
            var claimed = await TryClaimAsync(database, redisKey, pendingJson);
            if (!claimed)
            {
                var existing = await ReadExistingAsync(database, redisKey);
                if (existing is null)
                {
                    claimed = await TryClaimAsync(database, redisKey, pendingJson);
                }

                if (!claimed)
                {
                    existing ??= await ReadExistingAsync(database, redisKey);
                    await HandleExistingAsync(context, existing, fingerprint);
                    return;
                }
            }

            await ExecuteClaimedRequestAsync(context, database, redisKey, pending, pendingJson);
        }
        catch (IdempotencyOutcomeUnknownException exception)
        {
            await WriteOutcomeUnknownAsync(context, exception);
            return;
        }
        catch (RedisException exception)
        {
            await WriteRedisUnavailableAsync(context, exception);
            return;
        }
        catch (TimeoutException exception)
        {
            await WriteRedisUnavailableAsync(context, exception);
            return;
        }

    }

    private async Task ExecuteClaimedRequestAsync(
        HttpContext context,
        IDatabase database,
        RedisKey redisKey,
        IdempotencyRecord pending,
        string pendingJson)
    {
        var originalBody = context.Response.Body;
        await using var responseBuffer = new MemoryStream();
        context.Response.Body = responseBuffer;
        var handlerCompleted = false;
        using var renewalCancellation = new CancellationTokenSource();
        var renewal = RenewClaimUntilCancelledAsync(
            database,
            redisKey,
            pendingJson,
            renewalCancellation.Token);

        try
        {
            await _next(context);
            handlerCompleted = true;
            await StopRenewalAsync(renewalCancellation, renewal);

            var completed = new IdempotencyRecord
            {
                State = CompletedState,
                Fingerprint = pending.Fingerprint,
                OwnerToken = pending.OwnerToken,
                StatusCode = context.Response.StatusCode,
                Headers = CaptureHeaders(context.Response.Headers),
                Body = responseBuffer.ToArray()
            };
            var completedJson = JsonSerializer.Serialize(completed);
            var stored = (long)await database.ScriptEvaluateAsync(
                CompleteScript,
                [redisKey],
                [pendingJson, completedJson, ToMilliseconds(_options.ResponseTtl)]) == 1;
            if (!stored)
            {
                throw new InvalidOperationException("The idempotency claim was lost before the response could be stored.");
            }

            context.Response.Body = originalBody;
            responseBuffer.Position = 0;
            await responseBuffer.CopyToAsync(originalBody, context.RequestAborted);
        }
        catch (Exception exception)
        {
            await StopRenewalAsync(renewalCancellation, renewal, suppressFailure: true);
            context.Response.Body = originalBody;
            if (handlerCompleted)
            {
                await TryExtendClaimAsync(database, redisKey, pendingJson);
                throw new IdempotencyOutcomeUnknownException(
                    "The endpoint completed, but its idempotent response could not be persisted.",
                    exception);
            }

            await TryReleaseClaimAsync(database, redisKey, pendingJson);
            throw;
        }
    }

    private async Task RenewClaimUntilCancelledAsync(
        IDatabase database,
        RedisKey key,
        string pendingJson,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(
            Math.Max(100, _options.ClaimTtl.TotalMilliseconds / 3));

        try
        {
            while (true)
            {
                await Task.Delay(interval, cancellationToken);
                var renewed = (long)await database.ScriptEvaluateAsync(
                    ExtendScript,
                    [key],
                    [pendingJson, ToMilliseconds(_options.ClaimTtl)]) == 1;
                if (!renewed)
                {
                    throw new IdempotencyOutcomeUnknownException(
                        "The idempotency claim was lost while the endpoint was executing.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task StopRenewalAsync(
        CancellationTokenSource cancellation,
        Task renewal,
        bool suppressFailure = false)
    {
        cancellation.Cancel();
        if (suppressFailure)
        {
            try
            {
                await renewal;
            }
            catch
            {
            }

            return;
        }

        await renewal;
    }

    private async Task HandleExistingAsync(
        HttpContext context,
        IdempotencyRecord? existing,
        string fingerprint)
    {
        if (existing is null)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Idempotency state unavailable",
                "The idempotency claim changed while the request was being evaluated.");
            return;
        }

        if (existing.State is not (PendingState or CompletedState))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Invalid idempotency state",
                "The stored idempotency response is invalid.");
            return;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(existing.Fingerprint),
                Encoding.UTF8.GetBytes(fingerprint)))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "Idempotency key reused with a different request",
                "Use a new Idempotency-Key when the method, route, query, caller, content type, or body changes.");
            return;
        }

        if (existing.State == PendingState)
        {
            context.Response.Headers.RetryAfter = "1";
            await WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "Request already in progress",
                "Another request with the same Idempotency-Key is still executing.");
            return;
        }

        if (existing.StatusCode is null || existing.Body is null)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Invalid idempotency state",
                "The stored idempotency response is incomplete.");
            return;
        }

        context.Response.StatusCode = existing.StatusCode.Value;
        foreach (var (name, values) in existing.Headers)
        {
            if (!HopByHopHeaders.Contains(name))
            {
                context.Response.Headers[name] = new StringValues(values);
            }
        }

        context.Response.Headers[ReplayHeader] = "true";
        context.Response.ContentLength = existing.Body.Length;
        await context.Response.Body.WriteAsync(existing.Body, context.RequestAborted);
    }

    private async Task<bool> TryClaimAsync(IDatabase database, RedisKey key, string pendingJson)
        => await database.StringSetAsync(
            key,
            pendingJson,
            _options.ClaimTtl,
            When.NotExists);

    private static async Task<IdempotencyRecord?> ReadExistingAsync(IDatabase database, RedisKey key)
    {
        var value = await database.StringGetAsync(key);
        if (!value.HasValue)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IdempotencyRecord>((string)value!);
        }
        catch (JsonException)
        {
            return new IdempotencyRecord
            {
                State = "invalid",
                Fingerprint = string.Empty,
                OwnerToken = string.Empty
            };
        }
    }

    private async Task TryReleaseClaimAsync(IDatabase database, RedisKey key, string pendingJson)
    {
        try
        {
            await database.ScriptEvaluateAsync(ReleaseScript, [key], [pendingJson]);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            _logger.LogError(exception, "Could not release failed idempotency claim {RedisKey}.", key);
        }
    }

    private async Task TryExtendClaimAsync(IDatabase database, RedisKey key, string pendingJson)
    {
        try
        {
            await database.ScriptEvaluateAsync(
                ExtendScript,
                [key],
                [pendingJson, ToMilliseconds(_options.ResponseTtl)]);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            _logger.LogCritical(exception, "Could not preserve completed idempotency claim {RedisKey}.", key);
        }
    }

    private async Task WriteRedisUnavailableAsync(HttpContext context, Exception exception)
    {
        _logger.LogError(exception, "Redis is unavailable for an idempotent write request.");
        context.Response.Clear();
        context.Response.Headers.RetryAfter = "1";
        await WriteProblemAsync(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "Idempotency service unavailable",
            "The request was not executed because its idempotency guarantee could not be established.");
    }

    private async Task WriteOutcomeUnknownAsync(HttpContext context, Exception exception)
    {
        _logger.LogCritical(exception, "An executed write could not be recorded by the idempotency service.");
        context.Response.Clear();
        await WriteProblemAsync(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "Idempotency outcome unavailable",
            "The request may have completed, but its response could not be recorded. Retry with the same Idempotency-Key after the service recovers.");
    }

    private bool TryReadKey(StringValues header, out string key)
    {
        key = header.Count == 1 ? header[0]?.Trim() ?? string.Empty : string.Empty;
        return key.Length is > 0 && key.Length <= _options.MaximumKeyLength;
    }

    private string CreateRedisKey(string idempotencyKey, string caller)
    {
        var keyHash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(caller + "\n" + idempotencyKey)));
        return $"idempotency:{_options.ServiceName}:{keyHash}";
    }

    private static async Task<string> CreateFingerprintAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        await using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        context.Request.Body.Position = 0;

        var query = JsonSerializer.Serialize(context.Request.Query
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                pair.Key,
                Values = pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray()
            }));
        var caller = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var prefix = string.Join('\n',
            context.Request.Method.ToUpperInvariant(),
            context.Request.PathBase + context.Request.Path,
            query,
            caller,
            context.Request.ContentType ?? string.Empty) + "\n";

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(prefix));
        hash.AppendData(body.GetBuffer().AsSpan(0, checked((int)body.Length)));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Dictionary<string, string[]> CaptureHeaders(IHeaderDictionary headers)
        => headers
            .Where(header => !HopByHopHeaders.Contains(header.Key))
            .ToDictionary(
                header => header.Key,
                header => header.Value.Select(value => value ?? string.Empty).ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static bool IsStateChanging(string method)
        => HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method)
            || HttpMethods.IsDelete(method);

    private static long ToMilliseconds(TimeSpan value)
        => checked((long)value.TotalMilliseconds);

    private static Task WriteProblemAsync(
        HttpContext context,
        int status,
        string title,
        string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(new
        {
            type = "https://httpstatuses.com/" + status.ToString(CultureInfo.InvariantCulture),
            title,
            status,
            detail
        });
    }

    private sealed class IdempotencyRecord
    {
        public required string State { get; init; }
        public required string Fingerprint { get; init; }
        public required string OwnerToken { get; init; }
        public int? StatusCode { get; init; }
        public Dictionary<string, string[]> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[]? Body { get; init; }
    }

    private sealed class IdempotencyOutcomeUnknownException : Exception
    {
        public IdempotencyOutcomeUnknownException(string message)
            : base(message)
        {
        }

        public IdempotencyOutcomeUnknownException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
