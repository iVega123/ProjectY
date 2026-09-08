using AutoMapper;
using MotoHub.DTOs;
using MotoHub.Entities;
using MotoHub.Models;
using MotoHub.Repositories;
using MotoHub.Services.RabbitMQ;

using ProjectY.Shared.Pagination;
using ProjectY.Shared.Validation;

namespace MotoHub.Services
{
    public class MotorcycleService : IMotorcycleService
    {
        private readonly IMotorcycleRepository _repository;
        private readonly IMapper _mapper;
        private readonly IMessagingPublisherService _messagingPublisherService;
        private readonly IMotorcycleRetirement _retirement;

        public MotorcycleService(
            IMotorcycleRepository repository,
            IMapper mapper,
            IMessagingPublisherService messagingPublisherService,
            IMotorcycleRetirement retirement)
        {
            _repository = repository;
            _mapper = mapper;
            _messagingPublisherService = messagingPublisherService;
            _retirement = retirement;
        }

        public async Task<CursorPage<MotorcycleDTO>> GetMotorcyclesAsync(string? cursor, int? pageSize)
        {
            var page = await _repository.GetPageAsync(cursor, pageSize);
            return new CursorPage<MotorcycleDTO>(
                _mapper.Map<IReadOnlyList<MotorcycleDTO>>(page.Items),
                page.NextCursor);
        }

        public async Task<MotorcycleDTO?> GetMotorcycleByLicensePlateAsync(string licensePlate)
        {
            var motorcycle = await _repository.GetByLicensePlateAsync(licensePlate);
            return _mapper.Map<MotorcycleDTO>(motorcycle);
        }

        public async Task<MotorcycleDTO?> GetMotorcycleByIdAsync(Guid id)
        {
            var motorcycle = await _repository.GetByIdAsync(id);
            return _mapper.Map<MotorcycleDTO>(motorcycle);
        }

        public void CreateMotorcycle(MotorcycleDTO motorcycleDto)
        {
            var motorcycle = _mapper.Map<Motorcycle>(motorcycleDto);
            _repository.Add(motorcycle);
        }

        /// <summary>
        /// Renomear uma moto voltou a ser o que sempre deveria ter sido: um
        /// UPDATE numa linha.
        ///
        /// Antes havia uma reserva a pedir ao serviço de aluguéis, porque lá a
        /// placa era a chave -- renomear significava reescrever todos os
        /// aluguéis da moto, e duas renomeações concorrentes podiam se cruzar no
        /// meio. Os aluguéis referenciam o id da moto, então não há nada a
        /// reescrever: a placa nova aparece no histórico inteiro por junção.
        /// </summary>
        public async Task UpdateMotorcycleAsync(string licensePlate, string newLicencePlate)
        {
            licensePlate = BrazilianLicensePlateAttribute.Normalize(licensePlate);
            newLicencePlate = BrazilianLicensePlateAttribute.Normalize(newLicencePlate);
            var existingMotorcycle = await _repository.GetByLicensePlateAsync(licensePlate);
            if (existingMotorcycle == null)
            {
                return;
            }

            if (existingMotorcycle.RetiredAtUtc is not null)
            {
                return;
            }

            if (string.Equals(licensePlate, newLicencePlate, StringComparison.Ordinal))
            {
                return;
            }

            if (_repository.LicensePlateExists(newLicencePlate))
            {
                throw new InvalidOperationException(
                    $"Motorcycle with plate {newLicencePlate} already exists.");
            }

            existingMotorcycle.LicensePlate = newLicencePlate;

            LicencePlateRabbitMQEntity licencePlateRabbitMQEntity = new LicencePlateRabbitMQEntity()
            {
                AggregateId = existingMotorcycle.Id.ToString(),
                newLicencePlate = newLicencePlate,
                oldLicencePlate = licensePlate,
            };

            // A linha do outbox entra no mesmo SaveChanges do UPDATE: ou os dois,
            // ou nenhum. É o efeito exatamente-uma-vez do ADR 0009, e aqui ele
            // custa uma ordem de chamada, não um protocolo.
            _messagingPublisherService.PublishLicenceUpdate(licencePlateRabbitMQEntity);
            _repository.Update(existingMotorcycle);
        }

        public async Task<OperationResult> DeleteMotorcycle(string licensePlate)
        {
            var existingMotorcycle = await _repository.GetByLicensePlateAsync(licensePlate);
            if (existingMotorcycle == null)
                return OperationResult.Fail($"Motorcycle with plate {licensePlate} not found.");

            var result = await _retirement.RetireAsync(
                existingMotorcycle.Id,
                DateTime.UtcNow,
                MotorcycleRetirementReasons.RequestedByAdministrator);
            return result switch
            {
                MotorcycleRetirementResult.Retired => OperationResult.Ok("Motorcycle successfully retired."),
                MotorcycleRetirementResult.AlreadyRetired => OperationResult.Ok("Motorcycle was already retired."),
                _ => OperationResult.Fail(
                    "Motorcycle has an active rental and cannot be retired.",
                    StatusCodes.Status409Conflict)
            };
        }

        public bool LicensePlateExists(string licensePlate)
        {
            return _repository.LicensePlateExists(licensePlate);
        }
    }
}
