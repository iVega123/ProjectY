namespace RentalOperations.CrossCutting.Model
{
    public class Motorcycle
    {
        public int year { get; set; }
        public string model { get; set; } = string.Empty;
        public string licensePlate { get; set; } = string.Empty;
        public DateTime? retiredAtUtc { get; set; }
        public string? retirementReason { get; set; }
    }
}
