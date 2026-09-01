namespace RiderManager.Entities
{
    public class UploadFileEntity
    {
        public required string UserId { get; set; }
        public required string FileName { get; set; }
        public required string FileUrl { get; set; }
        public DateTime ExpiryDate { get; set; }
    }
}
