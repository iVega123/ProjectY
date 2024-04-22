namespace RiderManager.Entities
{
    public class ImagePart
    {
        public required string UserId { get; set; }
        public int SequenceNumber { get; set; }
        public string FileName { get; set; } = string.Empty;
        public byte[]? Content { get; set; }
        public bool EndOfFile { get; set; }
    }

    public class RiderMQEntity
    {
        public required string Email { get; set; }
        public required string Name { get; set; }
        public required string CNPJ { get; set; }
        public DateTime DateOfBirth { get; set; }
        public required string CNHNumber { get; set; }
        public required string CNHType { get; set; }
        public required string UserId { get; set; }
    }
}
