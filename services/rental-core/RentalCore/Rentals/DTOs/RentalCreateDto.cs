namespace RentalOperations.DTOs
{
    public class RentalCreateDto
    {
        public required string MotocycleLicencePlate { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime PredictedEndDate { get; set; }
    }
}
