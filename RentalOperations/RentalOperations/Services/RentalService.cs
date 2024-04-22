using AutoMapper;
using RentalOperations.CrossCutting.Services;
using RentalOperations.Domain;
using RentalOperations.DTOs;
using RentalOperations.Model;
using RentalOperations.Repository;

namespace RentalOperations.Services
{
    public class RentalService : IRentalService
    {
        private readonly IRentalRepository _repository;
        private readonly IMapper _mapper;
        private readonly IRiderManagerService _riderManagerService;
        private readonly IMotorcycleService _motorcycleService;

        public RentalService(
            IRentalRepository repository,
            IMapper mapper,
            IRiderManagerService riderManagerService,
            IMotorcycleService motorcycleService)
        {
            _repository = repository;
            _mapper = mapper;
            _motorcycleService = motorcycleService;
            _riderManagerService = riderManagerService;
        }

        public async Task CreateRentalAsync(RentalCreateDto createDto, string userId)
        {

            if(createDto.StartDate.AddDays(1) >= createDto.PredictedEndDate)
            {
                throw new InvalidOperationException("The Rent time must at least one day");
            }

            var existingRentals = await _repository.GetRentalsByMotorcycleIdAsync(createDto.MotocycleLicencePlate);
            foreach (var rent in existingRentals)
            {
                if (createDto.StartDate < rent.EndDate || createDto.PredictedEndDate > rent.StartDate)
                {
                    throw new InvalidOperationException("This motorcycle is already rented for the requested period.");
                }
            }

            var rider = await _riderManagerService.GetRiderByIdAsync(userId);
            if (rider == null)
            {
                throw new ArgumentException("Rider does not exist.");
            }
            if (!(rider.CNHType == "A" || rider.CNHType == "AB"))
            {
                throw new ArgumentException("Rider does not have the correct license type.");
            }

            var motorcycle = await _motorcycleService.GetMotorcycleByIdAsync(createDto.MotocycleLicencePlate);
            if (motorcycle == null)
            {
                throw new ArgumentException("Motorcycle does not exist.");
            }

            var rentalDomain = RentalDomain.Create(createDto, userId);
            var rental = new Rental
            {
                MotorcycleLicencePlate = rentalDomain.MotocycleLicencePlate,
                UserId = rentalDomain.UserId,
                StartDate = rentalDomain.StartDate,
                EndDate = rentalDomain.EndDate,
                PredictedEndDate = rentalDomain.PredictedEndDate,
                InitCost = rentalDomain.TotalCost
            };

            await _repository.CreateRentalAsync(rental);
        }

        public async Task<ResponseRentalDTO> CalculateFinalCostAsync(string rentalId, string userId, DateTime actualEndDate)
        {
            var rental = await _repository.GetRentalByIdAsync(rentalId);
            
            if (rental == null)
                throw new KeyNotFoundException($"No rental found with ID {rentalId}");
            
            if (rental.FinalCost > 0)
                return _mapper.Map<ResponseRentalDTO>(rental);

            var response = _mapper.Map<ResponseRentalDTO>(rental);
            response.ActualEndDate = actualEndDate;

            int daysPlanned = (rental.PredictedEndDate - rental.StartDate).Days;
            decimal dailyRate = DetermineDailyRate(daysPlanned);

            if (actualEndDate < rental.PredictedEndDate)
            {
                decimal penaltyRate = GetPenaltyRate(daysPlanned);
                int daysEarly = (rental.PredictedEndDate - actualEndDate).Days;
                response.AdditionalCostsOrSavings = -(daysEarly * dailyRate * penaltyRate);
                response.StatusMessage = "Return was early. A penalty was applied.";
            }
            else if (actualEndDate > rental.PredictedEndDate)
            {
                int daysLate = (actualEndDate - rental.PredictedEndDate).Days;
                response.AdditionalCostsOrSavings = daysLate * 50.00m;
                response.StatusMessage = "Return was late. Additional cost for extra days.";
            }
            else
            {
                response.StatusMessage = "Returned on the predicted end date. No additional costs.";
            }

            response.FinalTotalCost = response.OriginalTotalCost + response.AdditionalCostsOrSavings;
            response.UserId = userId;

            var updateRent = _mapper.Map<Rental>(response);
            await _repository.UpdateRentalAsync(updateRent);
            return response;
        }

        public async Task<List<ResponseRentalDTO>> GetRentalsByUserIdAsync(string userId)
        {
            var rentals = await _repository.GetRentalsByUserId(userId);
            var rentalDtos = _mapper.Map<List<ResponseRentalDTO>>(rentals);
            return rentalDtos;
        }

        public async Task UpdateMotorcycleLicensePlateAsync(string oldLicensePlate, string newLicensePlate)
        {
            await _repository.UpdateLicensePlateForAllRentalsAsync(oldLicensePlate, newLicensePlate);
        }

        public async Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate)
        {
            return await _repository.IsMotorcycleCurrentlyRentedAsync(licencePlate);
        }

        private decimal DetermineDailyRate(int days)
        {
            if (days <= 7) return 30m;
            if (days <= 15) return 28m;
            if (days <= 30) return 22m;
            if (days <= 45) return 20m;
            return 18m;
        }

        private decimal GetPenaltyRate(int days)
        {
            if (days <= 7) return 0.20m;
            return 0.40m;
        }
    }
}
