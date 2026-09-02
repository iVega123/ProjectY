using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ProjectY.Shared.Security;

public static class GatewayIdentityDefaults
{
    public const string AuthenticationScheme = "GatewayIdentity";
    public const string KeyIdHeader = "x-identity-key-id";
    public const string SubjectHeader = "x-identity-subject";
    public const string RolesHeader = "x-identity-roles";
    public const string IssuedAtHeader = "x-identity-issued-at";
    public const string SignatureHeader = "x-identity-signature";

    internal static readonly string[] Headers =
    [
        KeyIdHeader,
        SubjectHeader,
        RolesHeader,
        IssuedAtHeader,
        SignatureHeader
    ];
}

public sealed class GatewayIdentityOptions : AuthenticationSchemeOptions
{
    public byte[] SigningKey { get; set; } = [];
    public string SigningKeyId { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public TimeSpan MaximumAge { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(5);
    public TimeProvider Clock { get; set; } = TimeProvider.System;
}

public static class GatewayIdentityServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayIdentityAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        string audience)
    {
        var signingKey = configuration["GatewayIdentity:SigningKey"]
            ?? throw new InvalidOperationException("GatewayIdentity:SigningKey is not configured.");
        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException(
                "GatewayIdentity:SigningKey must contain at least 32 bytes.");
        }

        var signingKeyId = configuration["GatewayIdentity:SigningKeyId"] ?? "local-v1";
        var maximumAge = configuration.GetValue("GatewayIdentity:MaximumAgeSeconds", 30);
        var clockSkew = configuration.GetValue("GatewayIdentity:ClockSkewSeconds", 5);
        if (maximumAge <= 0 || clockSkew < 0)
        {
            throw new InvalidOperationException(
                "Gateway identity age must be positive and clock skew must not be negative.");
        }

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = GatewayIdentityDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = GatewayIdentityDefaults.AuthenticationScheme;
                options.DefaultForbidScheme = GatewayIdentityDefaults.AuthenticationScheme;
            })
            .AddScheme<GatewayIdentityOptions, GatewayIdentityAuthenticationHandler>(
                GatewayIdentityDefaults.AuthenticationScheme,
                options =>
                {
                    options.SigningKey = Encoding.UTF8.GetBytes(signingKey);
                    options.SigningKeyId = signingKeyId;
                    options.Audience = audience;
                    options.MaximumAge = TimeSpan.FromSeconds(maximumAge);
                    options.ClockSkew = TimeSpan.FromSeconds(clockSkew);
                });
        services.AddAuthorization();
        services.AddHttpContextAccessor();
        services.AddSingleton(new GatewayIdentitySigner(signingKey, signingKeyId));
        return services;
    }

    public static IHttpClientBuilder AddGatewayIdentityPropagation(
        this IHttpClientBuilder builder,
        string targetAudience,
        string? backgroundServiceSubject = null)
    {
        ClaimsPrincipal? backgroundServicePrincipal = null;
        if (backgroundServiceSubject is not null)
        {
            if (string.IsNullOrWhiteSpace(backgroundServiceSubject))
            {
                throw new ArgumentException(
                    "The background service subject cannot be empty.",
                    nameof(backgroundServiceSubject));
            }

            backgroundServicePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, backgroundServiceSubject),
                new Claim(ClaimTypes.Name, backgroundServiceSubject)
            ], GatewayIdentityDefaults.AuthenticationScheme));
        }

        return builder.AddHttpMessageHandler(services => new GatewayIdentityPropagationHandler(
            services.GetRequiredService<IHttpContextAccessor>(),
            services.GetRequiredService<GatewayIdentitySigner>(),
            targetAudience,
            backgroundServicePrincipal));
    }
}

public sealed class GatewayIdentityAuthenticationHandler(
    IOptionsMonitor<GatewayIdentityOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<GatewayIdentityOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presentHeaders = GatewayIdentityDefaults.Headers.Count(Request.Headers.ContainsKey);
        if (presentHeaders == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        if (presentHeaders != GatewayIdentityDefaults.Headers.Length
            || !TryGetSingleHeader(GatewayIdentityDefaults.KeyIdHeader, out var keyId)
            || !TryGetSingleHeader(GatewayIdentityDefaults.SubjectHeader, out var subject)
            || !TryGetSingleHeader(GatewayIdentityDefaults.RolesHeader, out var rolesValue)
            || !TryGetSingleHeader(GatewayIdentityDefaults.IssuedAtHeader, out var issuedAtValue)
            || !TryGetSingleHeader(GatewayIdentityDefaults.SignatureHeader, out var signatureValue))
        {
            return Task.FromResult(AuthenticateResult.Fail("Incomplete gateway identity envelope."));
        }

        if (!string.Equals(keyId, Options.SigningKeyId, StringComparison.Ordinal)
            || !IsSafeComponent(keyId, 128)
            || !IsSafeComponent(subject, 512)
            || !long.TryParse(issuedAtValue, NumberStyles.None, CultureInfo.InvariantCulture, out var issuedAt))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid gateway identity envelope."));
        }

        var now = Options.Clock.GetUtcNow().ToUnixTimeSeconds();
        if (issuedAt > now + (long)Options.ClockSkew.TotalSeconds
            || issuedAt < now - (long)Options.MaximumAge.TotalSeconds)
        {
            return Task.FromResult(AuthenticateResult.Fail("Expired gateway identity envelope."));
        }

        var roles = ParseRoles(rolesValue);
        if (roles is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid gateway identity roles."));
        }

        var pathAndQuery = $"{Request.PathBase}{Request.Path}{Request.QueryString}";
        var canonical = GatewayIdentitySigner.Canonicalize(
            keyId,
            subject,
            rolesValue,
            issuedAtValue,
            Request.Method,
            pathAndQuery,
            Options.Audience);
        if (!GatewayIdentitySigner.Verify(Options.SigningKey, canonical, signatureValue))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid gateway identity signature."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subject),
            new(ClaimTypes.Name, subject)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, GatewayIdentityDefaults.AuthenticationScheme));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, GatewayIdentityDefaults.AuthenticationScheme)));
    }

    private bool TryGetSingleHeader(string name, out string value)
    {
        value = string.Empty;
        if (!Request.Headers.TryGetValue(name, out var values) || values.Count != 1)
        {
            return false;
        }
        value = values[0] ?? string.Empty;
        return true;
    }

    private static string[]? ParseRoles(string value)
    {
        if (value.Length == 0)
        {
            return [];
        }
        if (value.Length > 1024)
        {
            return null;
        }
        var roles = value.Split(',', StringSplitOptions.None);
        return roles.Length <= 32 && roles.All(role => IsSafeComponent(role, 512))
            ? roles
            : null;
    }

    private static bool IsSafeComponent(string value, int maximumLength)
    {
        return value.Length is > 0
            && value.Length <= maximumLength
            && value.All(character => character is >= '!' and <= '+' or >= '-' and <= '~');
    }
}

public sealed class GatewayIdentitySigner
{
    private readonly byte[] _signingKey;
    private readonly string _signingKeyId;
    private readonly TimeProvider _clock;

    public GatewayIdentitySigner(
        string signingKey,
        string signingKeyId,
        TimeProvider? clock = null)
    {
        _signingKey = Encoding.UTF8.GetBytes(signingKey);
        _signingKeyId = signingKeyId;
        _clock = clock ?? TimeProvider.System;
    }

    public void Sign(HttpRequestMessage request, ClaimsPrincipal principal, string audience)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("An authenticated identity is required.");
        }
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("The identity subject is missing.");
        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var rolesValue = string.Join(',', roles);
        var issuedAt = _clock.GetUtcNow().ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var pathAndQuery = request.RequestUri switch
        {
            { IsAbsoluteUri: true } uri => uri.PathAndQuery,
            { } uri => uri.OriginalString,
            null => throw new InvalidOperationException("The outgoing request URI is missing.")
        };
        var canonical = Canonicalize(
            _signingKeyId,
            subject,
            rolesValue,
            issuedAt,
            request.Method.Method,
            pathAndQuery,
            audience);
        var signature = Base64UrlEncode(HMACSHA256.HashData(
            _signingKey,
            Encoding.UTF8.GetBytes(canonical)));

        foreach (var header in GatewayIdentityDefaults.Headers)
        {
            request.Headers.Remove(header);
        }
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.KeyIdHeader, _signingKeyId);
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.SubjectHeader, subject);
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.RolesHeader, rolesValue);
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.IssuedAtHeader, issuedAt);
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.SignatureHeader, $"v1={signature}");
    }

    internal static string Canonicalize(
        string keyId,
        string subject,
        string roles,
        string issuedAt,
        string method,
        string pathAndQuery,
        string audience)
    {
        return $"v1\n{keyId}\n{subject}\n{roles}\n{issuedAt}\n{method}\n{pathAndQuery}\n{audience}";
    }

    internal static bool Verify(byte[] key, string canonical, string signatureValue)
    {
        if (!signatureValue.StartsWith("v1=", StringComparison.Ordinal)
            || !TryBase64UrlDecode(signatureValue[3..], out var supplied))
        {
            return false;
        }
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
        return expected.Length == supplied.Length
            && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryBase64UrlDecode(string value, out byte[] decoded)
    {
        decoded = [];
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => "invalid"
        };
        try
        {
            decoded = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class GatewayIdentityPropagationHandler(
    IHttpContextAccessor httpContextAccessor,
    GatewayIdentitySigner signer,
    string targetAudience,
    ClaimsPrincipal? backgroundServicePrincipal = null) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext is { } httpContext
            ? httpContext.User
            : backgroundServicePrincipal
                ?? throw new InvalidOperationException(
                    "Gateway identity propagation requires an active HTTP request or a configured background service identity.");
        signer.Sign(request, principal, targetAudience);
        return base.SendAsync(request, cancellationToken);
    }
}
