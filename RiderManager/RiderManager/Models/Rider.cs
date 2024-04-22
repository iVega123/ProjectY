namespace RiderManager.Models
{
    public class Rider
    {
        public required string Id { get; set; }
        public required string UserId { get; set; }
        public required string Email { get; set; }
        public required string Name { get; set; }
        public required string CNPJ { get; set; }
        public DateTime DateOfBirth { get; set; }
        public required string CNHNumber { get; set; }
        public required string CNHType { get; set; }
        public PresignedUrl? CNHUrl { get; set; }
    }
}
