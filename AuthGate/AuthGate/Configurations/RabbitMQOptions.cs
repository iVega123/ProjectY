namespace AuthGate.Configurations
{
    public class RabbitMQOptions
    {
        public required string HostName { get; set; }
        public required string UserName { get; set; }
        public required string Password { get; set; }
        public required string RiderInfoQueueName { get; set; }
        public required string ImageStreamQueueName { get; set; }
    }
}
