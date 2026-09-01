using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace ProjectY.Shared.Validation;

public static partial class BrazilianCnpj
{
    public static string Normalize(string value) => DigitsRegex().Replace(value, string.Empty);

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !CnpjFormatRegex().IsMatch(value))
        {
            return false;
        }

        var digits = Normalize(value);
        if (digits.Distinct().Count() == 1)
        {
            return false;
        }

        return CalculateDigit(digits[..12], [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]) == digits[12] - '0'
            && CalculateDigit(digits[..13], [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]) == digits[13] - '0';
    }

    private static int CalculateDigit(string digits, int[] weights)
    {
        var sum = digits.Select((digit, index) => (digit - '0') * weights[index]).Sum();
        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }

    [GeneratedRegex(@"[^0-9]")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"^(?:[0-9]{14}|[0-9]{2}\.[0-9]{3}\.[0-9]{3}/[0-9]{4}-[0-9]{2})$")]
    private static partial Regex CnpjFormatRegex();
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class CnpjAttribute : ValidationAttribute
{
    public CnpjAttribute()
        : base("O CNPJ deve ser válido e estar no formato XX.XXX.XXX/XXXX-XX ou conter 14 dígitos.")
    {
    }

    public override bool IsValid(object? value) => value is null || value is string text && BrazilianCnpj.IsValid(text);
}
