namespace RiderManager.Models
{
    public class PresignedUrl
    {
        public string? Id { get; set; }
        public string? Url { get; set; }
        public string? ObjectName { get; set; }
        public DateTime Expiry { get; set; }
        public string? RiderId { get; set; }
        public Rider? Rider { get; set; }
    }
}
