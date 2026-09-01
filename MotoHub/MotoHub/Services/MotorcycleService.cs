using AutoMapper;
using MotoHub.CrossCutting;
using MotoHub.DTOs;
using MotoHub.Entities;
using MotoHub.Models;
using MotoHub.Repositories;
using MotoHub.Services.RabbitMQ;

using ProjectY.Shared.Pagination;

namespace MotoHub.Services
{
    public class MotorcycleService : IMotorcycleService
    {
        private readonly IMotorcycleRepository _repository;
        private readonly IMapper _mapper;
        private readonly IMessagingPublisherService _messagingPublisherService;
        private readonly IRentalOperationService _rentalOperationService;

        public MotorcycleService(
            IMotorcycleRepository repository,
            IMapper mapper,
            IMessagingPublisherService messagingPublisherService,
            IRentalOperationService rentalOperationService)
        {
            _repository = repository;
            _mapper = mapper;
            _messagingPublisherService = messagingPublisherService;
            _rentalOperationService = rentalOperationService;
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

        public void CreateMotorcycle(MotorcycleDTO motorcycleDto)
        {
            var motorcycle = _mapper.Map<Motorcycle>(motorcycleDto);
            _repository.Add(motorcycle);
        }

        public async Task UpdateMotorcycleAsync(string licensePlate, string newLicencePlate)
        {
            var existingMotorcycle = await _repository.GetByLicensePlateAsync(licensePlate);
            if (existingMotorcycle == null)
            {
                return;
            }

            if (existingMotorcycle.RetiredAtUtc is not null)
            {
                return;
            }

            existingMotorcycle.LicensePlate = newLicencePlate;

            LicencePlateRabbitMQEntity licencePlateRabbitMQEntity = new LicencePlateRabbitMQEntity()
            {
                AggregateId = existingMotorcycle.Id,
                newLicencePlate = newLicencePlate,
                oldLicencePlate = licensePlate,
            };

            _messagingPublisherService.PublishLicenceUpdate(licencePlateRabbitMQEntity);
            _repository.Update(existingMotorcycle);
        }

        public async Task<OperationResult> DeleteMotorcycle(string licensePlate)
        {
            var existingMotorcycle = await _repository.GetByLicensePlateAsync(licensePlate);
            if (existingMotorcycle == null)
                return OperationResult.Fail($"Motorcycle with plate {licensePlate} not found.");

            if (existingMotorcycle.RetiredAtUtc is not null)
                return OperationResult.Ok("Motorcycle was already retired.");

            try
            {
                var retirementReserved = await _rentalOperationService.TryRetireMotorcycleAsync(licensePlate);
                if (!retirementReserved)
                    return OperationResult.Fail(
                        "Motorcycle has an active rental and cannot be retired.",
                        StatusCodes.Status409Conflict);

                var retired = await _repository.RetireAsync(
                    existingMotorcycle.Id,
                    DateTime.UtcNow,
                    MotorcycleRetirementReasons.RequestedByAdministrator);
                return retired
                    ? OperationResult.Ok("Motorcycle successfully retired.")
                    : OperationResult.Ok("Motorcycle was already retired.");
            }
            catch (Exception ex)
            {
                // The RentalOperations retirement marker is intentionally retained on an
                // ambiguous failure. A retry can finish the soft delete without allowing a
                // rental to slip through the cross-service commit window.
                return OperationResult.Fail("Failed to retire the motorcycle due to an unexpected error. " + ex.Message);
            }
        }

        public async Task EnsureHistoricalReferencesAsync(IEnumerable<string> licensePlates)
        {
            var retiredAtUtc = DateTime.UtcNow;
            foreach (var licensePlate in licensePlates
                         .Where(plate => !string.IsNullOrWhiteSpace(plate))
                         .Select(plate => plate.Trim())
                         .Distinct(StringComparer.Ordinal))
            {
                await _repository.EnsureHistoricalReferenceAsync(licensePlate, retiredAtUtc);
            }
        }


        public bool LicensePlateExists(string licensePlate)
        {
            return _repository.LicensePlateExists(licensePlate);
        }
    }
}
