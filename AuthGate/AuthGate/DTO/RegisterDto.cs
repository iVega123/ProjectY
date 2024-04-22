using System.ComponentModel.DataAnnotations;

namespace AuthGate.DTO
{
    public class RegisterDto
    {
        [Required]
        [EmailAddress]
        public required string Email { get; set; }

        [Required]
        [StringLength(40, MinimumLength = 8)]
        public required string Password { get; set; }
    }
}
