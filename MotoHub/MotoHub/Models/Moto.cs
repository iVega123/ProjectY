namespace MotoHub.Models
{
    public class Motorcycle
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public int Year { get; set; }
        public string? Model { get; set; }
        public required string LicensePlate { get; set; }
        public DateTime RegistrationDate { get; set; }
    }
}
