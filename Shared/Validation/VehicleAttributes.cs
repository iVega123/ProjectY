using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace ProjectY.Shared.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed partial class BrazilianLicensePlateAttribute : ValidationAttribute
{
    public BrazilianLicensePlateAttribute()
        : base("A placa deve estar no formato ABC1234 ou ABC1D23.")
    {
    }

    public override bool IsValid(object? value) => value is null
        || value is string text && LicensePlateRegex().IsMatch(text.Trim());

    public static string Normalize(string value) => value.Trim().ToUpperInvariant();

    [GeneratedRegex(@"^(?:[A-Za-z]{3}[0-9]{4}|[A-Za-z]{3}[0-9][A-Za-z][0-9]{2})$")]
    private static partial Regex LicensePlateRegex();
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class PlausibleVehicleYearAttribute : ValidationAttribute
{
    private const int MinimumYear = 1900;

    public override bool IsValid(object? value) => value is null
        || value is int year && year >= MinimumYear && year <= DateTime.UtcNow.Year + 1;

    public override string FormatErrorMessage(string name) =>
        $"O ano deve estar entre {MinimumYear} e {DateTime.UtcNow.Year + 1}.";
}
