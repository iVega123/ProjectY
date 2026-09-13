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

    /// <summary>The signature that also covers the request body (issue #191).</summary>
    public const string SignatureV2Header = "x-identity-signature-v2";

    /// <summary>
    /// The largest body the gateway signs. A larger body did not come from it, and reading
    /// past this would hand anyone who reaches the port free memory before any signature is checked.
    /// </summary>
    public const int MaxSignedBodyBytes = 32 * 1024 * 1024;

    internal static readonly string[] Headers =
    [
        KeyIdHeader,
        SubjectHeader,
        RolesHeader,
        IssuedAtHeader,
        SignatureHeader,
        SignatureV2Header
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
    /// <summary>
    /// Two canonical strings live side by side until issue #191 is finished. <c>v1</c> binds the
    /// envelope to key, subject, roles, time, method, path and audience; <c>v2</c> adds the SHA-256
    /// of the body, which is what stops a captured envelope from carrying a different body on the
    /// same route inside its window. When the <c>v2</c> signature is present it alone decides.
    /// Without it <c>v1</c> is still accepted, because refusing it before the gateway signs
    /// <c>v2</c> would lock this service out — the failure of issue #136.
    /// </summary>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presentHeaders = GatewayIdentityDefaults.Headers.Count(Request.Headers.ContainsKey);
        if (presentHeaders == 0)
        {
            return AuthenticateResult.NoResult();
        }
        if (!TryGetSingleHeader(GatewayIdentityDefaults.KeyIdHeader, out var keyId)
            || !TryGetSingleHeader(GatewayIdentityDefaults.SubjectHeader, out var subject)
            || !TryGetSingleHeader(GatewayIdentityDefaults.RolesHeader, out var rolesValue)
            || !TryGetSingleHeader(GatewayIdentityDefaults.IssuedAtHeader, out var issuedAtValue)
            || !TryGetSignature(out var version, out var signatureValue))
        {
            return AuthenticateResult.Fail("Incomplete gateway identity envelope.");
        }

        if (!string.Equals(keyId, Options.SigningKeyId, StringComparison.Ordinal)
            || !IsSafeComponent(keyId, 128)
            || !IsSafeComponent(subject, 512)
            || !long.TryParse(issuedAtValue, NumberStyles.None, CultureInfo.InvariantCulture, out var issuedAt))
        {
            return AuthenticateResult.Fail("Invalid gateway identity envelope.");
        }

        var now = Options.Clock.GetUtcNow().ToUnixTimeSeconds();
        if (issuedAt > now + (long)Options.ClockSkew.TotalSeconds
            || issuedAt < now - (long)Options.MaximumAge.TotalSeconds)
        {
            return AuthenticateResult.Fail("Expired gateway identity envelope.");
        }

        var roles = ParseRoles(rolesValue);
        if (roles is null)
        {
            return AuthenticateResult.Fail("Invalid gateway identity roles.");
        }

        var pathAndQuery = $"{Request.PathBase}{Request.Path}{Request.QueryString}";
        var bound = GatewayIdentitySigner.Bind(
            keyId,
            subject,
            rolesValue,
            issuedAtValue,
            Request.Method,
            pathAndQuery,
            Options.Audience);
        string canonical;
        if (version == "v2")
        {
            // Falling back to v1 when v2 fails would accept exactly the substituted body v2 refused.
            var digest = await DigestBodyAsync();
            if (digest is null)
            {
                return AuthenticateResult.Fail("Unreadable or oversized request body.");
            }
            canonical = $"v2\n{bound}\n{digest}";
        }
        else
        {
            canonical = $"v1\n{bound}";
        }
        if (!GatewayIdentitySigner.Verify(Options.SigningKey, canonical, signatureValue, version))
        {
            return AuthenticateResult.Fail("Invalid gateway identity signature.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subject),
            new(ClaimTypes.Name, subject)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, GatewayIdentityDefaults.AuthenticationScheme));
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, GatewayIdentityDefaults.AuthenticationScheme));
    }

    /// <summary>Each signature at most once, and at least one of the two.</summary>
    private bool TryGetSignature(out string version, out string value)
    {
        var v1 = Request.Headers[GatewayIdentityDefaults.SignatureHeader];
        var v2 = Request.Headers[GatewayIdentityDefaults.SignatureV2Header];
        version = string.Empty;
        value = string.Empty;
        if (v1.Count > 1 || v2.Count > 1 || v1.Count + v2.Count == 0)
        {
            return false;
        }
        (version, value) = v2.Count == 1 ? ("v2", v2[0] ?? string.Empty) : ("v1", v1[0] ?? string.Empty);
        return true;
    }

    /// <summary>
    /// The last line of v2: the SHA-256 of the body bytes, read to their end whether or not a
    /// Content-Length came, in lowercase hex. No body and an empty body are the same digest.
    /// The body is rewound afterwards, so model binding reads it as it would have without this.
    /// </summary>
    private async Task<string?> DigestBodyAsync()
    {
        Request.EnableBuffering();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long total = 0;
        try
        {
            int read;
            while ((read = await Request.Body.ReadAsync(buffer, Context.RequestAborted)) > 0)
            {
                total += read;
                if (total > GatewayIdentityDefaults.MaxSignedBodyBytes)
                {
                    return null;
                }
                hash.AppendData(buffer, 0, read);
            }
        }
        catch (BadHttpRequestException)
        {
            // Kestrel's own body limit, or a client that broke off mid-upload.
            return null;
        }
        Request.Body.Position = 0;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
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

    /// <summary>
    /// Signs both envelope versions, as the gateway does: v1 for verifiers that have not rolled,
    /// and v2 over the bytes of <see cref="HttpRequestMessage.Content"/>. Reading the content
    /// buffers it, so the bytes sent are the bytes signed.
    /// </summary>
    public async Task SignAsync(
        HttpRequestMessage request,
        ClaimsPrincipal principal,
        string audience,
        CancellationToken cancellationToken = default)
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
        var bound = Bind(
            _signingKeyId,
            subject,
            rolesValue,
            issuedAt,
            request.Method.Method,
            pathAndQuery,
            audience);
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var v1 = Sign($"v1\n{bound}");
        var v2 = Sign($"v2\n{bound}\n{BodyDigest(body)}");

        foreach (var header in GatewayIdentityDefaults.Headers)
        {
            request.Headers.Remove(header);
        }
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.KeyIdHeader, _signingKeyId);
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.SubjectHeader, subject);
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.RolesHeader, rolesValue);
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.IssuedAtHeader, issuedAt);
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.SignatureHeader, $"v1={v1}");
        request.Headers.TryAddWithoutValidation(GatewayIdentityDefaults.SignatureV2Header, $"v2={v2}");
    }

    private string Sign(string canonical) =>
        Base64UrlEncode(HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(canonical)));

    /// <summary>Every canonical line after the version, up to and including the audience.</summary>
    internal static string Bind(
        string keyId,
        string subject,
        string roles,
        string issuedAt,
        string method,
        string pathAndQuery,
        string audience)
    {
        return $"{keyId}\n{subject}\n{roles}\n{issuedAt}\n{method}\n{pathAndQuery}\n{audience}";
    }

    /// <summary>The SHA-256 of the body in lowercase hex; no body is the digest of zero bytes.</summary>
    public static string BodyDigest(ReadOnlySpan<byte> body) =>
        Convert.ToHexStringLower(SHA256.HashData(body));

    internal static bool Verify(byte[] key, string canonical, string signatureValue, string version)
    {
        var prefix = version + "=";
        if (!signatureValue.StartsWith(prefix, StringComparison.Ordinal)
            || !TryBase64UrlDecode(signatureValue[prefix.Length..], out var supplied))
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
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext is { } httpContext
            ? httpContext.User
            : backgroundServicePrincipal
                ?? throw new InvalidOperationException(
                    "Gateway identity propagation requires an active HTTP request or a configured background service identity.");
        await signer.SignAsync(request, principal, targetAudience, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
