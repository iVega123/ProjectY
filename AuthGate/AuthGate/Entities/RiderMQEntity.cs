namespace AuthGate.Entities
{
    public class RiderMQEntity
    {
        public required string Email { get; set; }
        public required string Name { get; set; }
        public required string CNPJ { get; set; }
        public DateTime DateOfBirth { get; set; }
        public required string CNHNumber {  get; set; }
        public required string CNHType { get; set;}
        public required string UserId { get; set; }
    }
}
