using AutoMapper;
using RiderManager.DTOs;
using RiderManager.Models;
using RiderManager.Repositories;

namespace RiderManager.Services.RiderServices
{
    public class RiderService : IRiderService
    {
        private readonly IRiderRepository _repository;
        private readonly IMapper _mapper;

        public RiderService(IRiderRepository repository, IMapper mapper)
        {
            _repository = repository;
            _mapper = mapper;
        }

        public async Task<IEnumerable<RiderResponseDTO>> GetAllRidersAsync()
        {
            var riders = await _repository.GetAllAsync();
            return _mapper.Map<IEnumerable<RiderResponseDTO>>(riders);
        }

        public async Task<RiderResponseDTO> GetRiderByUserIdAsync(string userId)
        {
            var rider = await _repository.GetByUserIdAsync(userId);
            return _mapper.Map<RiderResponseDTO>(rider);
        }

        public async Task<RiderResponseDTO> AddRiderAsync(RiderDTO riderDto)
        {
            var rider = _mapper.Map<Rider>(riderDto);
            await _repository.AddAsync(rider);
            return _mapper.Map<RiderResponseDTO>(rider);
        }

        public async Task UpdateRiderAsync(string userId, RiderDTO riderDto)
        {
            var rider = await _repository.GetByUserIdAsync(userId);
            if (rider == null)
            {
                // Handle situation where Rider does not exist
                return;
            }
            
            _mapper.Map(riderDto, rider);
            
            await _repository.UpdateAsync(rider);
        }

        public async Task DeleteRiderAsync(string userId)
        {
            var rider = await _repository.GetByUserIdAsync(userId);
            if (rider == null)
            {
                // Handle situation where Rider does not exist
                return;
            }

            await _repository.DeleteAsync(rider.Id);
        }
    }
}
