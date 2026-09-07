using System.Text.Json.Serialization;

namespace MotoHub.Entities
{
    public class LicencePlateRabbitMQEntity
    {
        [JsonIgnore]
        public string AggregateId { get; set; } = string.Empty;

        public string oldLicencePlate { get; set; } = string.Empty;

        public string newLicencePlate { get; set; } = string.Empty;
    }
}
