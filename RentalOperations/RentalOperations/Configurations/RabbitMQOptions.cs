namespace RentalOperations.Configurations
{
    public class RabbitMQOptions
    {
        public required string HostName { get; set; }
        public required string UserName { get; set; }
        public required string Password { get; set; }
        public required string LicenceUpdateQueueName { get; set; }
        public required string LicenceUpdatePoisonQueueName { get; set; }
    }
}
