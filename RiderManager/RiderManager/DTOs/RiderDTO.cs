using System.ComponentModel.DataAnnotations;

namespace RiderManager.DTOs
{
    public class RiderDTO
    {
        [Required]
        [StringLength(20)]
        [RegularExpression(@"([0-9]{2}[\.]?[0-9]{3}[\.]?[0-9]{3}[\/]?[0-9]{4}[-]?[0-9]{2})",
        ErrorMessage = "O CNPJ deve estar no formato XX.XXX.XXX/XXXX-XX")]
        public required string CNPJ { get; set; }

        public required string Name { get; set; }
        
        public required string Email { get; set; }


        public DateTime DateOfBirth { get; set; }

        [StringLength(11)]
        [RegularExpression(@"^[0-9]{11}$", ErrorMessage = "O número da CNH deve conter 11 dígitos")]
        public required string CNHNumber { get; set; }

        public required string CNHType { get; set; }

        public required string UserId { get; set; }

        public IFormFile? CNHImagePath { get; set; }
    }
}
