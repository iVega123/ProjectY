namespace MotoHub.Models
{
    public class Motorcycle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Year { get; set; }
        public string? Model { get; set; }
        public required string LicensePlate { get; set; }
        public DateTime RegistrationDate { get; set; }
        public DateTime? RetiredAtUtc { get; set; }
        public string? RetirementReason { get; set; }
    }
}
