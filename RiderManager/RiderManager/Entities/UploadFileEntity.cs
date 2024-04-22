namespace RiderManager.Entities
{
    public class UploadFileEntity
    {
        public required string riderId { get; set; }
        public required string fileName { get; set; }
        public required string fileUrl { get; set; }
        public DateTime expiryDate { get; set; }
    }
}
