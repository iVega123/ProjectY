namespace RiderManager.Configurations
{
    public class MinIOOptions
    {
        public required string Endpoint { get; set; }
        public required string AccessKey { get; set; }
        public required string SecretKey { get; set; }
        public required string BucketName { get; set; }
    }
}
