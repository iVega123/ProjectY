using MongoDB.Bson.Serialization.Attributes;

namespace RentalOperations.Model;

public sealed class MotorcycleClaim
{
    [BsonId]
    public required string MotorcycleLicencePlate { get; init; }

    [BsonElement("kind")]
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public MotorcycleClaimKind Kind { get; init; }

    [BsonElement("rentalId")]
    [BsonIgnoreIfNull]
    public string? RentalId { get; init; }

    [BsonElement("createdAtUtc")]
    public DateTime CreatedAtUtc { get; init; }
}

public enum MotorcycleClaimKind
{
    ActiveRental,
    Retired
}
