using AutoMapper;
using RentalOperations.CrossCutting.Services;
using RentalOperations.Domain;
using RentalOperations.DTOs;
using RentalOperations.Model;
using RentalOperations.Repository;

using ProjectY.Shared.Pagination;
using ProjectY.Shared.Validation;

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
            createDto.MotocycleLicencePlate = BrazilianLicensePlateAttribute.Normalize(
                createDto.MotocycleLicencePlate);
            if (createDto.StartDate.AddDays(1) >= createDto.PredictedEndDate)
            {
                throw new InvalidOperationException("The Rent time must at least one day");
            }

            if (await _repository.HasOverlappingRentalAsync(
                createDto.MotocycleLicencePlate,
                createDto.StartDate,
                createDto.PredictedEndDate))
            {
                throw new ActiveRentalConflictException(createDto.MotocycleLicencePlate);
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
            if (motorcycle.retiredAtUtc is not null)
            {
                throw new MotorcycleRetiredException(createDto.MotocycleLicencePlate);
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

            var rentalId = rental._id!.Value.ToString();
            var claimResult = await _repository.TryClaimRentalAsync(
                rental.MotorcycleLicencePlate,
                rentalId);
            if (claimResult == MotorcycleClaimResult.Retired)
            {
                throw new MotorcycleRetiredException(rental.MotorcycleLicencePlate);
            }
            if (claimResult == MotorcycleClaimResult.ActiveRental)
            {
                throw new ActiveRentalConflictException(rental.MotorcycleLicencePlate);
            }

            // The claim is deliberately retained if MongoDB reports an ambiguous
            // insert failure. Startup reconciliation can repair a stale claim; releasing
            // it here could let retirement win after the rental was actually committed.
            await _repository.CreateRentalAsync(rental);
        }

        public async Task<ResponseRentalDTO> CalculateFinalCostAsync(string rentalId, string userId, DateTime actualEndDate)
        {
            var rental = await _repository.GetRentalByIdAsync(rentalId);

            if (rental == null)
                throw new KeyNotFoundException($"No rental found with ID {rentalId}");

            if (!string.Equals(rental.UserId, userId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The rental belongs to another rider.");

            if (rental.Status == RentalStatus.Completed)
            {
                await _repository.ReleaseRentalClaimAsync(
                    rental.MotorcycleLicencePlate,
                    rental._id!.Value.ToString());
                return _mapper.Map<ResponseRentalDTO>(rental);
            }

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

            rental.EndDate = actualEndDate;
            rental.FinalCost = response.FinalTotalCost;
            rental.AdditionalCostsOrSavings = response.AdditionalCostsOrSavings;
            rental.StatusMessage = response.StatusMessage;
            rental.Status = RentalStatus.Completed;
            await _repository.UpdateRentalAsync(rental);
            await _repository.ReleaseRentalClaimAsync(
                rental.MotorcycleLicencePlate,
                rental._id!.Value.ToString());
            return response;
        }

        public async Task<CursorPage<ResponseRentalDTO>> GetRentalsByUserIdAsync(
            string userId,
            string? cursor,
            int? pageSize)
        {
            var page = await _repository.GetRentalsByUserId(userId, cursor, pageSize);
            return new CursorPage<ResponseRentalDTO>(
                _mapper.Map<IReadOnlyList<ResponseRentalDTO>>(page.Items),
                page.NextCursor);
        }

        public async Task UpdateMotorcycleLicensePlateAsync(string oldLicensePlate, string newLicensePlate)
        {
            await _repository.UpdateLicensePlateForAllRentalsAsync(
                BrazilianLicensePlateAttribute.Normalize(oldLicensePlate),
                BrazilianLicensePlateAttribute.Normalize(newLicensePlate));
        }

        public Task<bool> TryReserveLicensePlateRenameAsync(
            string oldLicensePlate,
            string newLicensePlate) =>
            _repository.TryReserveLicensePlateRenameAsync(
                BrazilianLicensePlateAttribute.Normalize(oldLicensePlate),
                BrazilianLicensePlateAttribute.Normalize(newLicensePlate));

        public async Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate)
        {
            return await _repository.IsMotorcycleCurrentlyRentedAsync(
                BrazilianLicensePlateAttribute.Normalize(licencePlate));
        }

        public async Task<bool> TryRetireMotorcycleAsync(string licencePlate)
        {
            var result = await _repository.TryClaimRetirementAsync(
                BrazilianLicensePlateAttribute.Normalize(licencePlate));
            return result is MotorcycleClaimResult.Acquired or MotorcycleClaimResult.Retired;
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
