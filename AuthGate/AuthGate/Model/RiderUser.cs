using System.ComponentModel.DataAnnotations;

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
        [RegularExpression(
            @"([0-9]{2}[\.]?[0-9]{3}[\.]?[0-9]{3}[\/]?[0-9]{4}[-]?[0-9]{2})", 
            ErrorMessage = "O CNPJ deve estar no formato XX.XXX.XXX/XXXX-XX")]
        public required string CNPJ { get; set; }
        
        public required string Name { get; set; }

        [Required]
        public DateTime DateOfBirth { get; set; }

        [Required]
        [StringLength(11)]
        [RegularExpression(@"((cnh.*[0-9]{11})|(CNH.*[0-9]{11})|(habilitação.*[0-9]{11})|(carteira.*[0-9]{11}))", ErrorMessage = "O número da CNH deve conter 11 dígitos")]
        public required string CNHNumber { get; set; }

        [Required]
        [EnumDataType(typeof(TipoCNH))]
        public TipoCNH CNHType { get; set; }
    }
}
