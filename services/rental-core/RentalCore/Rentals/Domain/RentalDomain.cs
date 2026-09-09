using RentalCore.Errors;
using RentalOperations.DTOs;

namespace RentalOperations.Domain
{
    public class RentalDomain
    {
        public Guid MotorcycleId { get; private set; }
        public string UserId { get; private set; } = string.Empty;
        public DateTime StartDate { get; private set; }
        public DateTime? EndDate { get; private set; }
        public DateTime PredictedEndDate { get; private set; }
        public decimal TotalCost { get; private set; }

        private RentalDomain() { }

        public static RentalDomain Create(RentalCreateDto dto, string userId)
        {
            ValidateDates(dto.StartDate, dto.PredictedEndDate);
            if (dto.MotorcycleId == Guid.Empty)
            {
                throw new InvalidRequestException("A motorcycle id is required.");
            }

            var domain = new RentalDomain
            {
                MotorcycleId = dto.MotorcycleId,
                UserId = userId,
                StartDate = dto.StartDate,
                EndDate = null,
                PredictedEndDate = dto.PredictedEndDate,
                TotalCost = CalculateTotalCost(dto.StartDate, dto.PredictedEndDate, userId)
            };

            return domain;
        }

        private static void ValidateDates(DateTime startDate, DateTime predictedEndDate)
        {
            if (startDate >= predictedEndDate)
                throw new InvalidRequestException("Start date must be before the end and predicted end dates.");
        }

        private static decimal CalculateTotalCost(DateTime startDate, DateTime predictedEndDate, string rider)
        {
            int totalDays = (predictedEndDate - startDate).Days;
            decimal dailyRate = RentalOperations.Services.LocalPricing.DailyRate(totalDays, rider);
            return totalDays * dailyRate;
        }

    }
}
