using System.ComponentModel.DataAnnotations;
using ProjectY.Shared.Validation;

namespace AuthGate.DTO
{
    public class RiderRegisterDto : RegisterDto
    {
        [Required]
        [StringLength(20)]
        [Cnpj]
        public required string CNPJ { get; set; }

        public required string Name { get; set; }

        public DateTime DateOfBirth { get; set; }

        [Required]
        [StringLength(11, MinimumLength = 11)]
        [RegularExpression(@"^[0-9]{11}$", ErrorMessage = "O número da CNH deve conter 11 dígitos")]
        public required string CNHNumber { get; set; }

        public required string CNHType { get; set; }

        public IFormFile? CNHImage { get; set; }
    }
}
