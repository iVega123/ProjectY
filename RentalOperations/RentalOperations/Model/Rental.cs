using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using System.Security.Cryptography;

namespace RentalOperations.Model
{
    public class Rental
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public ObjectId? _id { get; set; }

        [BsonElement("MotorcycleLicencePlate")]
        public required string MotorcycleLicencePlate { get; set; }

        [BsonElement("userId")]
        public required string UserId { get; set; }

        [BsonElement("startDate")]
        public DateTime StartDate { get; set; }

        [BsonElement("endDate")]
        public DateTime EndDate { get; set; }

        [BsonElement("predictedEndDate")]
        public DateTime PredictedEndDate { get; set; }

        [BsonElement("initCost")]
        public decimal InitCost { get; set; }

        [BsonElement("finalCost")]
        public decimal FinalCost { get; set; }

        [BsonElement("additionalCostsOrSavings")]
        public decimal AdditionalCostsOrSavings { get; set; }

        [BsonElement("statusMessage")]
        public string StatusMessage { get; set; } = string.Empty;

        public Rental()
        {
            _id = ObjectId.GenerateNewId();
        }
    }

    
}
