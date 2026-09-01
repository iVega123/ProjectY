using System.ComponentModel.DataAnnotations;
using ProjectY.Shared.Validation;

namespace MotoHub.DTOs
{
    public class MotorcycleDTO
    {
        [PlausibleVehicleYear]
        public int Year { get; set; }

        [Required]
        [StringLength(100, MinimumLength = 2)]
        public string? Model { get; set; }

        [Required]
        [StringLength(7, MinimumLength = 7)]
        [BrazilianLicensePlate]
        public required string LicensePlate { get; set; }

        public DateTime? RetiredAtUtc { get; set; }
        public string? RetirementReason { get; set; }
    }
}
