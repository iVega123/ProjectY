using ProjectY.Shared.Validation;
using Xunit;

namespace AuthGateTests.Unit.Validators;

public sealed class BrazilianCnpjTests
{
    [Theory]
    [InlineData("92.805.586/0001-80")]
    [InlineData("92805586000180")]
    public void IsValid_AcceptsPublishedFormatsWithValidCheckDigits(string value)
    {
        Assert.True(BrazilianCnpj.IsValid(value));
    }

    [Theory]
    [InlineData("prefix92.805.586/0001-80")]
    [InlineData("92.805.586/0001-80suffix")]
    [InlineData("12.345.678/0001-00")]
    [InlineData("11111111111111")]
    public void IsValid_RejectsSurroundingJunkAndInvalidCheckDigits(string value)
    {
        Assert.False(BrazilianCnpj.IsValid(value));
    }

    [Fact]
    public void Normalize_RemovesFormatting()
    {
        Assert.Equal("92805586000180", BrazilianCnpj.Normalize("92.805.586/0001-80"));
    }
}
