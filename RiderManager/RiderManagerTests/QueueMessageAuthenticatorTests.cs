using ProjectY.Shared.Messaging;
using RiderManager.Entities;

namespace RiderManagerTests;

public class QueueMessageAuthenticatorTests
{
    private const string SigningKey = "test-only-signing-key-with-at-least-32-bytes";

    [Fact]
    public void ValidateEnvelope_RejectsClaimedUserIdThatDoesNotMatchSignedIdentity()
    {
        var authenticator = new QueueMessageAuthenticator(SigningKey);
        var forgedPayload = CreateRider("forged-user-id");
        var message = authenticator.CreateEnvelope(
            "rider.registration.v1",
            "authenticated-user-id",
            forgedPayload);

        var exception = Assert.Throws<QueueMessageAuthenticationException>(() =>
            authenticator.ValidateEnvelope<RiderMQEntity>(
                message,
                "rider.registration.v1",
                payload => payload.UserId));

        Assert.Contains("does not match", exception.Message);
    }

    [Fact]
    public void ValidateEnvelope_RejectsUnsignedDirectQueueMessage()
    {
        var authenticator = new QueueMessageAuthenticator(SigningKey);
        const string directMessage = "{\"UserId\":\"forged-user-id\",\"Email\":\"attacker@example.com\"}";

        Assert.Throws<QueueMessageAuthenticationException>(() =>
            authenticator.ValidateEnvelope<RiderMQEntity>(
                directMessage,
                "rider.registration.v1",
                payload => payload.UserId));
    }

    [Fact]
    public void ValidateEnvelope_AcceptsSignedPayloadWithMatchingIdentity()
    {
        var authenticator = new QueueMessageAuthenticator(SigningKey);
        var payload = CreateRider("authenticated-user-id");
        var message = authenticator.CreateEnvelope(
            "rider.registration.v1",
            payload.UserId,
            payload);

        var validatedPayload = authenticator.ValidateEnvelope<RiderMQEntity>(
            message,
            "rider.registration.v1",
            candidate => candidate.UserId);

        Assert.Equal(payload.UserId, validatedPayload.UserId);
        Assert.Equal(payload.Email, validatedPayload.Email);
    }

    private static RiderMQEntity CreateRider(string userId) => new()
    {
        UserId = userId,
        Email = "rider@example.com",
        Name = "Test Rider",
        CNPJ = "12345678000199",
        DateOfBirth = new DateTime(1990, 1, 1),
        CNHNumber = "12345678900",
        CNHType = "A"
    };
}
