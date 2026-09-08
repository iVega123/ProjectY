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

        /// <summary>
        /// Quantos aluguéis um lote pode pedir de uma vez.
        ///
        /// Um lote sem teto é uma consulta arbitrária escrita pelo cliente, e o
        /// #138 mede a tela por número de chamadas, não por tamanho de resposta.
        /// </summary>
        public const int MaxBatchSize = 100;

        public async Task CreateRentalAsync(RentalCreateDto createDto, string userId)
        {
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
                () => _motorcycleService.GetMotorcycleByIdAsync(createDto.MotorcycleId));
            if (motorcycle == null)
            {
                throw new ArgumentException("Motorcycle does not exist.");
            }
            if (motorcycle.retiredAtUtc is not null)
            {
                throw new MotorcycleRetiredException(createDto.MotorcycleId);
            }

            // Uma sobreposição futura não é a mesma coisa que uma dupla reserva
            // agora, e o índice único parcial só recusa a segunda. Esta checagem
            // cobre a agenda; a corrida continua sendo decidida no INSERT.
            if (await BeforeWriteAsync(() => _repository.HasOverlappingRentalAsync(
                createDto.MotorcycleId,
                createDto.StartDate,
                createDto.PredictedEndDate)))
            {
                throw new ActiveRentalConflictException(createDto.MotorcycleId);
            }

            var rentalDomain = RentalDomain.Create(createDto, userId);
            var rental = new Rental
            {
                MotorcycleId = rentalDomain.MotorcycleId,
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

        /// <summary>
        /// A leitura em lote do #138.
        ///
        /// Um id que não pertence a quem pediu não vira 403: vira ausência. O
        /// lote devolve o que o chamador pode ver, e "existe, mas não é seu"
        /// seria mais do que ele precisa saber -- transformaria o endpoint num
        /// oráculo de existência para quem quisesse varrer ids.
        /// </summary>
        public async Task<IReadOnlyList<ResponseRentalDTO>> GetRentalsByIdsAsync(
            IReadOnlyCollection<Guid> ids,
            string userId,
            bool isAdmin)
        {
            var rentals = await _repository.GetRentalsByIdsAsync(ids);
            var visible = isAdmin
                ? rentals
                : rentals.Where(rental => string.Equals(rental.UserId, userId, StringComparison.Ordinal))
                         .ToList();
            return _mapper.Map<IReadOnlyList<ResponseRentalDTO>>(visible);
        }

        public async Task<bool> IsMotorcycleCurrentlyRentedAsync(Guid motorcycleId)
        {
            return await _repository.IsMotorcycleCurrentlyRentedAsync(motorcycleId);
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
