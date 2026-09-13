using AutoMapper;
using RentalOperations.Domain;
using RentalOperations.DTOs;
using RentalOperations.Model;
using RentalOperations.Repository;
using RentalCore.Errors;

using ProjectY.Shared.Pagination;
using ProjectY.Shared.Validation;

namespace RentalOperations.Services
{
    public class RentalService : IRentalService
    {
        private readonly IRentalRepository _repository;
        private readonly IMapper _mapper;

        public RentalService(IRentalRepository repository, IMapper mapper)
        {
            _repository = repository;
            _mapper = mapper;
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
                throw new InvalidRequestException("The rental must last at least one day.");
            }

            // Read locally, never over the network. Calling identity here would put
            // the fat event back on the request path it exists to remove, so this is
            // asserted by a test rather than left to reviewer discipline. The rider,
            // the motorcycle and the schedule come back in one read: each separate
            // read paid the database's latency again (#206).
            var preconditions = await BeforeWriteAsync(() => _repository.ReadCreationPreconditionsAsync(
                userId,
                createDto.MotorcycleId,
                createDto.StartDate,
                createDto.PredictedEndDate));
            var rider = preconditions.Rider;
            if (rider == null)
            {
                throw new RiderProjectionPendingException(userId);
            }
            if (!rider.Verified)
            {
                throw new RiderNotEntitledException();
            }

            if (preconditions.Motorcycle == MotorcycleAvailability.Missing)
            {
                throw new ResourceNotFoundException("The motorcycle does not exist.");
            }
            if (preconditions.Motorcycle == MotorcycleAvailability.Retired)
            {
                throw new MotorcycleRetiredException(createDto.MotorcycleId);
            }

            // Uma sobreposição futura não é a mesma coisa que uma dupla reserva
            // agora, e o índice único parcial só recusa a segunda. Esta checagem
            // cobre a agenda; a corrida continua sendo decidida no INSERT.
            if (preconditions.Overlaps)
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

        /// <summary>
        /// Fecha o aluguel. Não calcula dinheiro.
        ///
        /// O que acontece aqui é uma transição de estado e uma data. Quanto se
        /// deve é decidido pelo billing, a partir do rental.closed que esta
        /// mesma transação enfileira -- porque liquidação e ciclo de vida do
        /// aluguel são contextos diferentes, e até o #137 dividiam esta classe.
        ///
        /// A consequência está à vista e vale ser dita: o valor devido passa a
        /// ser eventualmente consistente. Fechar devolve o aluguel fechado, não
        /// a fatura; ela existe alguns instantes depois, quando o evento chega.
        /// </summary>
        public async Task<ResponseRentalDTO> CloseRentalAsync(string rentalId, string userId, DateTime actualEndDate)
        {
            var rental = await BeforeWriteAsync(() => _repository.GetRentalByIdAsync(rentalId));

            if (rental == null)
                throw new ResourceNotFoundException("The rental does not exist.");

            if (!string.Equals(rental.UserId, userId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The rental belongs to another rider.");

            // Fechar de novo devolve o mesmo aluguel em vez de enfileirar um
            // segundo rental.closed: o inbox do billing reconheceria a
            // repetição, mas não há razão para produzi-la.
            if (rental.Status == RentalStatus.Completed)
            {
                return _mapper.Map<ResponseRentalDTO>(rental);
            }

            // Uma devolução antes do início não existe, e o billing não tem como
            // recusá-la: para ele seriam zero dias usados e o plano inteiro em
            // multa. A data é entrada de usuário, e a validação pertence a quem
            // a recebe.
            if (actualEndDate < rental.StartDate)
            {
                throw new InvalidRequestException("A rental cannot end before it starts.");
            }

            rental.EndDate = actualEndDate;
            rental.Status = RentalStatus.Completed;
            await _repository.UpdateRentalAsync(rental);
            return _mapper.Map<ResponseRentalDTO>(rental);
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

    }
}
