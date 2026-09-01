using System.ComponentModel.DataAnnotations;
using ProjectY.Shared.Validation;

namespace AuthGate.Model
{
    public enum TipoCNH
    {
        A,
        B,
        AB
    }

    public class RiderUser : ApplicationUser
    {
        [Required]
        [StringLength(20)]
        [Cnpj]
        public required string CNPJ { get; set; }

        public required string Name { get; set; }

        [Required]
        public DateTime DateOfBirth { get; set; }

        [Required]
        [StringLength(11, MinimumLength = 11)]
        [RegularExpression(@"^[0-9]{11}$", ErrorMessage = "O número da CNH deve conter 11 dígitos")]
        public required string CNHNumber { get; set; }

        [Required]
        [EnumDataType(typeof(TipoCNH))]
        public TipoCNH CNHType { get; set; }
    }
}
