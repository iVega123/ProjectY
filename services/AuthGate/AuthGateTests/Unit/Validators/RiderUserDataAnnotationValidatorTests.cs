using AuthGate.Model;
using AuthGate.Validators;
using Xunit;

namespace AuthGateTests.Unit.Validators;

public sealed class RiderUserDataAnnotationValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsExactlyElevenCnhDigits()
    {
        var validator = new RiderUserDataAnnotationValidator();
        var rider = CreateRider("12345678901");

        var result = await validator.ValidateAsync(null!, rider);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("CNH12345678901")]
    public async Task ValidateAsync_RejectsInvalidCnh(string cnh)
    {
        var validator = new RiderUserDataAnnotationValidator();
        var rider = CreateRider(cnh);

        var result = await validator.ValidateAsync(null!, rider);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Description.Contains("CNH", StringComparison.OrdinalIgnoreCase));
    }

    private static RiderUser CreateRider(string cnh) => new()
    {
        UserName = "rider@example.com",
        Email = "rider@example.com",
        CNPJ = "92805586000180",
        Name = "Rider",
        DateOfBirth = new DateTime(1990, 1, 1),
        CNHNumber = cnh,
        CNHType = TipoCNH.A
    };
}
