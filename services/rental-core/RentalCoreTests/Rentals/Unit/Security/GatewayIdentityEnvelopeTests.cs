using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ProjectY.Shared.Security;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

namespace RentalOperationsTests.Unit.Security;

/// <summary>
/// The envelope of ADR 0008 against an envelope the gateway produces, not one this file signs.
///
/// The golden values are the ones <c>signs_the_v2_envelopes_the_verifiers_pin</c> in
/// services/api-gateway/src/auth.rs proves the gateway emits for this route. They were computed
/// separately, with openssl, from the canonical string written in ADR 0008. That test pins what
/// the gateway signs; this one pins what rental-core accepts, and what rental-core's own service
/// signer emits — it is the one verifier that also signs.
/// </summary>
public sealed class GatewayIdentityEnvelopeTests
{
    private const string Key = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
    private const string KeyId = "local-v1";
    private const string Audience = "projecty.rental-core";
    private const long IssuedAt = 1_789_300_000;
    private const string RentalPath = "/api/rental";
    private const string Body = """{"motorcycleId":"00000000-0000-0000-0000-000000000001","plan":7}""";
    private const string GoldenV2 = "v2=idQJwvMxcuM0EEsGGXa8tvOm32Dwcya9HJGh0vz8QwE";

    /// <summary>
    /// The v1 signature a gateway from before issue #274 sent alongside this envelope, in the
    /// header below. It covers no body.
    /// </summary>
    private const string PreviousV1 = "v1=3vAv26TC3UzJTX5cWizLQx42_D7y7a4m9ZBX3r29gWY";
    private const string LegacySignatureHeader = "x-identity-signature";

    [Fact]
    public async Task AV2EnvelopeTheGatewaySigned_IsAccepted_AndTheBodyIsLeftForTheController()
    {
        var context = Captured(Body);

        var result = await AuthenticateAsync(context);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal("rider-123", result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        using var reader = new StreamReader(context.Request.Body);
        Assert.Equal(Body, await reader.ReadToEndAsync());
    }

    /// <summary>Issue #191: the same envelope, on the same route, inside its window, with another body.</summary>
    [Theory]
    [InlineData("""{"motorcycleId":"00000000-0000-0000-0000-000000000002","plan":7}""")]
    [InlineData(Body + " ")]
    [InlineData("")]
    public async Task ACapturedEnvelope_WithAnotherBody_IsRefused(string substituted)
    {
        var result = await AuthenticateAsync(Captured(substituted));

        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// What a gateway from before issue #274 sent, minus the v2 signature: an envelope this
    /// verifier accepted until then, with the very body it was signed for.
    /// </summary>
    [Fact]
    public async Task AV1OnlyEnvelope_IsRefused()
    {
        var context = Captured(Body);
        context.Request.Headers.Remove(GatewayIdentityDefaults.SignatureV2Header);
        context.Request.Headers[LegacySignatureHeader] = PreviousV1;

        Assert.False((await AuthenticateAsync(context)).Succeeded);
    }

    /// <summary>
    /// The downgrade issue #274 closes: a captured envelope from the previous gateway, carrying
    /// both signatures, with the v2 header stripped and the body substituted. The v1 left behind
    /// is valid for that body, because it covers no body at all.
    /// </summary>
    [Theory]
    [InlineData("""{"motorcycleId":"00000000-0000-0000-0000-000000000002","plan":7}""")]
    [InlineData("")]
    public async Task StrippingV2_FromACapturedEnvelope_DoesNotFallBackToV1(string substituted)
    {
        var context = Captured(substituted);
        context.Request.Headers[LegacySignatureHeader] = PreviousV1;
        context.Request.Headers.Remove(GatewayIdentityDefaults.SignatureV2Header);

        Assert.False((await AuthenticateAsync(context)).Succeeded);
    }

    /// <summary>
    /// During the rollout of issue #274 the previous gateway still sends both signatures to this
    /// verifier. That passes on v2, and the v1 that came along does not rescue a substituted body.
    /// </summary>
    [Fact]
    public async Task TheV1OfAPreviousGateway_IsIgnored()
    {
        var original = Captured(Body);
        original.Request.Headers[LegacySignatureHeader] = PreviousV1;
        var substituted = Captured("{}");
        substituted.Request.Headers[LegacySignatureHeader] = PreviousV1;

        Assert.True((await AuthenticateAsync(original)).Succeeded);
        Assert.False((await AuthenticateAsync(substituted)).Succeeded);
    }

    [Fact]
    public async Task TheServiceSigner_EmitsTheSameEnvelopeAsTheGateway()
    {
        var signer = new GatewayIdentitySigner(Key, KeyId, new FixedClock(IssuedAt));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://rental-core" + RentalPath)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json")
        };

        await signer.SignAsync(request, Rider(), Audience);

        Assert.Equal(GoldenV2, Single(request, GatewayIdentityDefaults.SignatureV2Header));
        Assert.False(request.Headers.Contains(LegacySignatureHeader));
        // Reading the content to sign it must not consume what is about to be sent.
        Assert.Equal(Body, await request.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A request that already carries a v1 signature, from reuse or a caller's own headers, leaves
    /// the signer without it: nothing signed here travels with a signature that ignores the body.
    /// </summary>
    [Fact]
    public async Task TheServiceSigner_StripsALegacySignatureTheRequestAlreadyCarried()
    {
        var signer = new GatewayIdentitySigner(Key, KeyId, new FixedClock(IssuedAt));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://rental-core" + RentalPath)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(LegacySignatureHeader, PreviousV1);

        await signer.SignAsync(request, Rider(), Audience);

        Assert.False(request.Headers.Contains(LegacySignatureHeader));
        Assert.Equal(GoldenV2, Single(request, GatewayIdentityDefaults.SignatureV2Header));
    }

    /// <summary>
    /// A slow upload must not spend the envelope's window before it is sent: the timestamp is
    /// taken after the body is read. The clock here reads a minute earlier until the content has
    /// been read, so stamping first would produce a different signature than the golden one.
    /// </summary>
    [Fact]
    public async Task TheServiceSigner_StampsTheEnvelopeAfterReadingTheBody()
    {
        var content = new ContentThatReportsItsRead(Encoding.UTF8.GetBytes(Body));
        var signer = new GatewayIdentitySigner(
            Key, KeyId, new MovingClock(() => content.WasRead ? IssuedAt : IssuedAt - 60));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://rental-core" + RentalPath)
        {
            Content = content
        };

        await signer.SignAsync(request, Rider(), Audience);

        Assert.Equal(GoldenV2, Single(request, GatewayIdentityDefaults.SignatureV2Header));
    }

    /// <summary>The empty-body rule, written down, so a GET does not depend on a guess.</summary>
    [Fact]
    public async Task NoBodyAndAnEmptyBody_AreTheDigestOfZeroBytes()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            GatewayIdentitySigner.BodyDigest([]));

        Assert.True((await AuthenticateAsync(Signed("GET", "/api/rental/user", [], body: null))).Succeeded);
        Assert.True((await AuthenticateAsync(Signed("GET", "/api/rental/user", [], new MemoryStream()))).Succeeded);
    }

    /// <summary>
    /// Without a Content-Length the digest is of the body read to its end, which is what the
    /// gateway signed, and the controller still gets the whole body afterwards.
    /// </summary>
    [Fact]
    public async Task AStreamedBody_IsHashedToItsEnd_AndLeftForTheController()
    {
        var bytes = Encoding.UTF8.GetBytes(Body);
        var context = Signed("POST", RentalPath, bytes, new NonSeekableStream(bytes.Length, bytes));

        var result = await AuthenticateAsync(context);

        Assert.True(result.Succeeded, result.Failure?.Message);
        using var reader = new StreamReader(context.Request.Body);
        Assert.Equal(Body, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ABodyTheGatewayCouldNotHaveSigned_IsRefused()
    {
        var huge = new NonSeekableStream(GatewayIdentityDefaults.MaxSignedBodyBytes + 1L, content: null);

        var result = await AuthenticateAsync(Signed("POST", RentalPath, [], huge));

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("twice")]
    [InlineData("carrying the v1 prefix")]
    [InlineData("the previous v1 in its place")]
    public async Task TheSignature_ComesExactlyOnce(string tampering)
    {
        var context = Captured(Body);
        var headers = context.Request.Headers;
        switch (tampering)
        {
            case "none":
                headers.Remove(GatewayIdentityDefaults.SignatureV2Header);
                break;
            case "twice":
                headers[GatewayIdentityDefaults.SignatureV2Header] = new StringValues([GoldenV2, GoldenV2]);
                break;
            case "carrying the v1 prefix":
                headers[GatewayIdentityDefaults.SignatureV2Header] = "v1=" + GoldenV2[3..];
                break;
            default:
                headers[GatewayIdentityDefaults.SignatureV2Header] = PreviousV1;
                break;
        }

        Assert.False((await AuthenticateAsync(context)).Succeeded);
    }

    // ------------------------------------------------------------------ support

    /// <summary>The golden envelope as the gateway sends it, with the given body.</summary>
    private static DefaultHttpContext Captured(string body)
    {
        var context = Request("POST", RentalPath, new MemoryStream(Encoding.UTF8.GetBytes(body)));
        context.Request.Headers[GatewayIdentityDefaults.SignatureV2Header] = GoldenV2;
        return context;
    }

    /// <summary>An envelope over <paramref name="signedBody"/>, signed here, sent with <paramref name="body"/>.</summary>
    private static DefaultHttpContext Signed(string method, string path, byte[] signedBody, Stream? body)
    {
        var bound = string.Join('\n',
            KeyId, "rider-123", "Rider", IssuedAt.ToString(CultureInfo.InvariantCulture), method, path, Audience);
        var canonical = $"v2\n{bound}\n{Convert.ToHexStringLower(SHA256.HashData(signedBody))}";
        var signature = Convert.ToBase64String(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(canonical)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var context = Request(method, path, body);
        context.Request.Headers[GatewayIdentityDefaults.SignatureV2Header] = "v2=" + signature;
        return context;
    }

    private static DefaultHttpContext Request(string method, string path, Stream? body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (body is not null)
        {
            context.Request.Body = body;
        }
        context.Request.Headers[GatewayIdentityDefaults.KeyIdHeader] = KeyId;
        context.Request.Headers[GatewayIdentityDefaults.SubjectHeader] = "rider-123";
        context.Request.Headers[GatewayIdentityDefaults.RolesHeader] = "Rider";
        context.Request.Headers[GatewayIdentityDefaults.IssuedAtHeader] =
            IssuedAt.ToString(CultureInfo.InvariantCulture);
        return context;
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(HttpContext context)
    {
        var options = new GatewayIdentityOptions
        {
            SigningKey = Encoding.UTF8.GetBytes(Key),
            SigningKeyId = KeyId,
            Audience = Audience,
            Clock = new FixedClock(IssuedAt)
        };
        var handler = new GatewayIdentityAuthenticationHandler(
            new FixedOptions(options), NullLoggerFactory.Instance, UrlEncoder.Default);
        await handler.InitializeAsync(
            new AuthenticationScheme(
                GatewayIdentityDefaults.AuthenticationScheme, null, typeof(GatewayIdentityAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    private static ClaimsPrincipal Rider() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "rider-123"),
            new Claim(ClaimTypes.Role, "Rider")
        ], GatewayIdentityDefaults.AuthenticationScheme));

    private static string Single(HttpRequestMessage request, string name) =>
        Assert.Single(request.Headers.GetValues(name));

    private sealed class FixedClock(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    private sealed class MovingClock(Func<long> unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds());
    }

    /// <summary>Content with no known length that records when it was read, as a stream upload does.</summary>
    private sealed class ContentThatReportsItsRead(byte[] bytes) : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(bytes);
            WasRead = true;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FixedOptions(GatewayIdentityOptions options) : IOptionsMonitor<GatewayIdentityOptions>
    {
        public GatewayIdentityOptions CurrentValue => options;

        public GatewayIdentityOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<GatewayIdentityOptions, string?> listener) => null;
    }

    /// <summary>A body with no length and no seeking, as a chunked upload arrives.</summary>
    private sealed class NonSeekableStream(long length, byte[]? content) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            if (read <= 0)
            {
                return 0;
            }
            if (content is null)
            {
                Array.Clear(buffer, offset, read);
            }
            else
            {
                Array.Copy(content, _position, buffer, offset, read);
            }
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
