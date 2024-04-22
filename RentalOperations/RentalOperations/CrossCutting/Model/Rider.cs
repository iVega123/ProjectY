namespace RentalOperations.CrossCutting.Model
{
    public class Rider
    {
        public string Id { get; set; } = string.Empty;
        public string CNPJ { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime DateOfBirth { get; set; }
        public string CNHNumber { get; set; } = string.Empty;
        public string CNHType { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string CNHUrl { get; set; } = string.Empty;
    }
}
