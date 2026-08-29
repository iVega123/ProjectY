using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectY.Shared.Messaging;

public sealed class QueueMessageEnvelope
{
    public int Version { get; init; }
    public required string MessageType { get; init; }
    public required string SubjectUserId { get; init; }
    public required string MessageId { get; init; }
    public long IssuedAtUnixSeconds { get; init; }
    public required string Payload { get; init; }
    public required string Signature { get; init; }
}

public sealed class QueueMessageAuthenticationException : Exception
{
    public QueueMessageAuthenticationException(string message)
        : base(message)
    {
    }

    public QueueMessageAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class QueueMessageAuthenticator
{
    private const int CurrentVersion = 1;
    private readonly byte[] _signingKey;

    public QueueMessageAuthenticator(string signingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        _signingKey = Encoding.UTF8.GetBytes(signingKey);

        if (_signingKey.Length < 32)
        {
            throw new ArgumentException("The queue message signing key must contain at least 32 UTF-8 bytes.", nameof(signingKey));
        }
    }

    public string CreateEnvelope<T>(string messageType, string subjectUserId, T payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectUserId);
        ArgumentNullException.ThrowIfNull(payload);

        var unsignedEnvelope = new QueueMessageEnvelope
        {
            Version = CurrentVersion,
            MessageType = messageType,
            SubjectUserId = subjectUserId,
            MessageId = Guid.NewGuid().ToString("D"),
            IssuedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)),
            Signature = string.Empty
        };

        var envelope = new QueueMessageEnvelope
        {
            Version = unsignedEnvelope.Version,
            MessageType = unsignedEnvelope.MessageType,
            SubjectUserId = unsignedEnvelope.SubjectUserId,
            MessageId = unsignedEnvelope.MessageId,
            IssuedAtUnixSeconds = unsignedEnvelope.IssuedAtUnixSeconds,
            Payload = unsignedEnvelope.Payload,
            Signature = Convert.ToBase64String(ComputeSignature(unsignedEnvelope))
        };

        return JsonSerializer.Serialize(envelope);
    }

    public T ValidateEnvelope<T>(string serializedEnvelope, string expectedMessageType, Func<T, string> getClaimedUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedEnvelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMessageType);
        ArgumentNullException.ThrowIfNull(getClaimedUserId);

        QueueMessageEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<QueueMessageEnvelope>(serializedEnvelope)
                ?? throw new QueueMessageAuthenticationException("The queue message envelope is empty.");
        }
        catch (QueueMessageAuthenticationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new QueueMessageAuthenticationException("The queue message is not a valid signed envelope.", exception);
        }

        ValidateMetadata(envelope, expectedMessageType);
        ValidateSignature(envelope);

        T payload;
        try
        {
            var payloadBytes = Convert.FromBase64String(envelope.Payload);
            payload = JsonSerializer.Deserialize<T>(payloadBytes)
                ?? throw new QueueMessageAuthenticationException("The signed queue message payload is empty.");
        }
        catch (QueueMessageAuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new QueueMessageAuthenticationException("The signed queue message payload is invalid.", exception);
        }

        var claimedUserId = getClaimedUserId(payload);
        if (string.IsNullOrWhiteSpace(claimedUserId)
            || !string.Equals(envelope.SubjectUserId, claimedUserId, StringComparison.Ordinal))
        {
            throw new QueueMessageAuthenticationException("The payload user identity does not match the signed subject.");
        }

        return payload;
    }

    private static void ValidateMetadata(QueueMessageEnvelope envelope, string expectedMessageType)
    {
        if (envelope.Version != CurrentVersion
            || string.IsNullOrWhiteSpace(envelope.MessageType)
            || string.IsNullOrWhiteSpace(envelope.SubjectUserId)
            || string.IsNullOrWhiteSpace(envelope.MessageId)
            || envelope.IssuedAtUnixSeconds <= 0
            || string.IsNullOrWhiteSpace(envelope.Payload)
            || string.IsNullOrWhiteSpace(envelope.Signature))
        {
            throw new QueueMessageAuthenticationException("The queue message envelope metadata is incomplete or unsupported.");
        }

        if (!string.Equals(envelope.MessageType, expectedMessageType, StringComparison.Ordinal))
        {
            throw new QueueMessageAuthenticationException("The queue message type is not valid for this consumer.");
        }
    }

    private void ValidateSignature(QueueMessageEnvelope envelope)
    {
        byte[] suppliedSignature;
        try
        {
            suppliedSignature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException exception)
        {
            throw new QueueMessageAuthenticationException("The queue message signature is malformed.", exception);
        }

        var expectedSignature = ComputeSignature(envelope);
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature))
        {
            throw new QueueMessageAuthenticationException("The queue message signature is invalid.");
        }
    }

    private byte[] ComputeSignature(QueueMessageEnvelope envelope)
    {
        var canonicalMessage = string.Join('\n',
            envelope.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.MessageType,
            envelope.SubjectUserId,
            envelope.MessageId,
            envelope.IssuedAtUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.Payload);

        return HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(canonicalMessage));
    }
}
