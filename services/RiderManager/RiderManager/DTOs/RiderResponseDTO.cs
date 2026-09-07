using System.ComponentModel.DataAnnotations;

namespace RiderManager.DTOs
{
    public class RiderResponseDTO
    {
        public required string Id { get; set; }

        public required string CNPJ { get; set; }

        public required string Name { get; set; }

        public DateTime DateOfBirth { get; set; }

        public required string CNHNumber { get; set; }

        public required string CNHType { get; set; }

        public required string UserId { get; set; }

        public string? CNHUrl { get; set; }
    }
}
