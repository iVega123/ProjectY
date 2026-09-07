using System.ComponentModel.DataAnnotations;

namespace AuthGate.DTO
{
    public class LogoutDto
    {
        [Required]
        [EmailAddress]
        public required string Email { get; set; }
    }
}
