using AutoMapper;
using MotoHub.CrossCutting;
using MotoHub.DTOs;
using MotoHub.Entities;
using MotoHub.Models;
using MotoHub.Repositories;
using MotoHub.Services.RabbitMQ;

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

        public IEnumerable<MotorcycleDTO> GetAllMotorcycles()
        {
            var motorcycles = _repository.GetAll();
            return _mapper.Map<IEnumerable<MotorcycleDTO>>(motorcycles);
        }

        public async Task<MotorcycleDTO> GetMotorcycleByLicensePlateAsync(string licensePlate)
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

            existingMotorcycle.LicensePlate = newLicencePlate;

            _repository.Update(existingMotorcycle);

            LicencePlateRabbitMQEntity licencePlateRabbitMQEntity = new LicencePlateRabbitMQEntity()
            {
                newLicencePlate = newLicencePlate,
                oldLicencePlate = licensePlate,
            };

            _messagingPublisherService.PublishLicenceUpdate(licencePlateRabbitMQEntity);
        }

        public async Task<OperationResult> DeleteMotorcycle(string licensePlate)
        {
            var existingMotorcycle = await _repository.GetByLicensePlateAsync(licensePlate);
            if (existingMotorcycle == null)
                return OperationResult.Fail($"Motorcycle with plate {licensePlate} not found.");

            var isRented = await _rentalOperationService.GetRentalsByMotorcycleLicencePlateAsync(licensePlate);
            if (isRented)
                return OperationResult.Fail("Motorcycle is currently rented and cannot be deleted.");

            try
            {
                _repository.Delete(existingMotorcycle.Id);
                return OperationResult.Ok("Motorcycle successfully deleted.");
            }
            catch (Exception ex)
            {
                return OperationResult.Fail("Failed to delete the motorcycle due to an unexpected error." + ex.Message);
            }
        }


        public bool LicensePlateExists(string licensePlate)
        {
            return _repository.LicensePlateExists(licensePlate);
        }
    }
}
