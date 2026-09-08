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
        private readonly IRiderProjectionStore _riders;
        private readonly IMotorcycleService _motorcycleService;

        public RentalService(
            IRentalRepository repository,
            IMapper mapper,
            IRiderProjectionStore riders,
            IMotorcycleService motorcycleService)
        {
            _repository = repository;
            _mapper = mapper;
            _motorcycleService = motorcycleService;
            _riders = riders;
        }

        public async Task CreateRentalAsync(RentalCreateDto createDto, string userId)
        {
            createDto.MotocycleLicencePlate = BrazilianLicensePlateAttribute.Normalize(
                createDto.MotocycleLicencePlate);
            if (createDto.StartDate.AddDays(1) >= createDto.PredictedEndDate)
            {
                throw new InvalidOperationException("The Rent time must at least one day");
            }

            // Read locally, never over the network. Calling identity here would put
            // the fat event back on the request path it exists to remove, so this is
            // asserted by a test rather than left to reviewer discipline.
            var rider = await BeforeWriteAsync(() => _riders.GetAsync(userId, CancellationToken.None));
            if (rider == null)
            {
                throw new RiderProjectionPendingException(userId);
            }
            if (!rider.Verified)
            {
                throw new ArgumentException("Rider does not have the correct license type.");
            }

            var motorcycle = await BeforeWriteAsync(
                () => _motorcycleService.GetMotorcycleByIdAsync(createDto.MotocycleLicencePlate));
            if (motorcycle == null)
            {
                throw new ArgumentException("Motorcycle does not exist.");
            }
            if (motorcycle.retiredAtUtc is not null)
            {
                throw new MotorcycleRetiredException(createDto.MotocycleLicencePlate);
            }
            if (!Guid.TryParse(motorcycle.id, out var motorcycleId))
            {
                throw new ArgumentException("Motorcycle does not exist.");
            }

            // Uma sobreposição futura não é a mesma coisa que uma dupla reserva
            // agora, e o índice único parcial só recusa a segunda. Esta checagem
            // cobre a agenda; a corrida continua sendo decidida no INSERT.
            if (await BeforeWriteAsync(() => _repository.HasOverlappingRentalAsync(
                motorcycleId,
                createDto.StartDate,
                createDto.PredictedEndDate)))
            {
                throw new ActiveRentalConflictException(createDto.MotocycleLicencePlate);
            }

            var rentalDomain = RentalDomain.Create(createDto, userId);
            var rental = new Rental
            {
                MotorcycleId = motorcycleId,
                MotorcycleLicencePlate = rentalDomain.MotocycleLicencePlate,
                UserId = rentalDomain.UserId,
                RiderName = rider.Name,
                StartDate = rentalDomain.StartDate,
                EndDate = rentalDomain.EndDate,
                PredictedEndDate = rentalDomain.PredictedEndDate,
                InitCost = rentalDomain.TotalCost
            };

            // Sem reserva prévia e sem nada a soltar depois: o aluguel e o evento
            // que o anuncia entram na mesma transação, e quem recusa a segunda
            // tentativa simultânea é o índice único parcial.
            await _repository.CreateRentalAsync(rental);
        }

        public async Task<ResponseRentalDTO> CalculateFinalCostAsync(string rentalId, string userId, DateTime actualEndDate)
        {
            var rental = await BeforeWriteAsync(() => _repository.GetRentalByIdAsync(rentalId));

            if (rental == null)
                throw new KeyNotFoundException($"No rental found with ID {rentalId}");

            if (!string.Equals(rental.UserId, userId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The rental belongs to another rider.");

            if (rental.Status == RentalStatus.Completed)
            {
                return _mapper.Map<ResponseRentalDTO>(rental);
            }

            var response = _mapper.Map<ResponseRentalDTO>(rental);
            response.ActualEndDate = actualEndDate;

            int daysPlanned = (rental.PredictedEndDate - rental.StartDate).Days;
            decimal dailyRate = daysPlanned > 0 ? rental.InitCost / daysPlanned : 0;

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

        public async Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate)
        {
            return await _repository.IsMotorcycleCurrentlyRentedAsync(
                BrazilianLicensePlateAttribute.Normalize(licencePlate));
        }

        private static async Task<T> BeforeWriteAsync<T>(Func<Task<T>> read)
        {
            try { return await read(); }
            catch (Exception ex) when (DependencyFailure.IsUnavailable(ex))
            {
                throw new PreWriteDependencyException(ex);
            }
        }

        private decimal GetPenaltyRate(int days)
        {
            if (days <= 7) return 0.20m;
            return 0.40m;
        }
    }
}
