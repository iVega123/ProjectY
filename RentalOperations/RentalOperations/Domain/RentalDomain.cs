using RentalOperations.DTOs;

namespace RentalOperations.Domain
{
    public class RentalDomain
    {
        public string MotocycleLicencePlate { get; private set; } = string.Empty;
        public string UserId { get; private set; } = string.Empty;
        public DateTime StartDate { get; private set; }
        public DateTime EndDate { get; private set; }
        public DateTime PredictedEndDate { get; private set; }
        public decimal TotalCost { get; private set; }

        private RentalDomain() { }

        public static RentalDomain Create(RentalCreateDto dto, string userId)
        {
            ValidateDates(dto.StartDate, dto.PredictedEndDate);

            var domain = new RentalDomain
            {
                MotocycleLicencePlate = dto.MotocycleLicencePlate,
                UserId = userId,
                StartDate = dto.StartDate,
                EndDate = DateTime.MinValue,
                PredictedEndDate = dto.PredictedEndDate,
                TotalCost = CalculateTotalCost(dto.StartDate, dto.PredictedEndDate)
            };

            return domain;
        }

        private static void ValidateDates(DateTime startDate, DateTime predictedEndDate)
        {
            if (startDate >= predictedEndDate)
                throw new ArgumentException("Start date must be before the end and predicted end dates.");
        }

        private static decimal CalculateTotalCost(DateTime startDate, DateTime predictedEndDate)
        {
            int totalDays = (predictedEndDate - startDate).Days;
            decimal dailyRate = DetermineDailyRate(totalDays);
            return totalDays * dailyRate;
        }

        private static decimal DetermineDailyRate(int days)
        {
            if (days <= 7) return 30m;
            if (days <= 15) return 28m;
            if (days <= 30) return 22m;
            if (days <= 45) return 20m;
            return 18m;
        }
    }
}