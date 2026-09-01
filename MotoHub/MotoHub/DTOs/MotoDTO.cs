namespace MotoHub.DTOs
{
    public class MotorcycleDTO
    {
        public int Year { get; set; }
        public string? Model { get; set; }
        public required string LicensePlate { get; set; }
        public DateTime? RetiredAtUtc { get; set; }
        public string? RetirementReason { get; set; }
    }
}
