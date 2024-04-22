namespace RentalOperations.DTOs
{
    public class ResponseRentalDTO
    {
        public string RentalId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string MotocycleLicencePlate { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime PredictedEndDate { get; set; }
        public DateTime ActualEndDate { get; set; }
        public decimal OriginalTotalCost { get; set; }
        public decimal FinalTotalCost { get; set; }
        public decimal AdditionalCostsOrSavings { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
    }

}
