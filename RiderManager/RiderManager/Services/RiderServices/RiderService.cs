using System.ComponentModel.DataAnnotations;
using AutoMapper;
using ProjectY.Shared.Pagination;
using ProjectY.Shared.Validation;
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

        public async Task<CursorPage<RiderResponseDTO>> GetRidersAsync(string? cursor, int? pageSize)
        {
            var page = await _repository.GetPageAsync(cursor, pageSize);
            var riders = _mapper.Map<IReadOnlyList<RiderResponseDTO>>(page.Items);
            var now = DateTime.UtcNow;
            for (var index = 0; index < riders.Count; index++)
            {
                if (page.Items[index].CNHUrl?.Expiry <= now)
                {
                    riders[index].CNHUrl = null;
                }
            }

            return new CursorPage<RiderResponseDTO>(
                riders,
                page.NextCursor);
        }

        public async Task<RiderResponseDTO> GetRiderByUserIdAsync(string userId)
        {
            var rider = await _repository.GetByUserIdAsync(userId);
            return _mapper.Map<RiderResponseDTO>(rider);
        }

        public async Task<RiderResponseDTO> AddRiderAsync(RiderDTO riderDto)
        {
            ValidateAndNormalize(riderDto);
            var rider = _mapper.Map<Rider>(riderDto);
            await _repository.AddAsync(rider);
            return _mapper.Map<RiderResponseDTO>(rider);
        }

        public async Task UpdateRiderAsync(string userId, RiderDTO riderDto)
        {
            ValidateAndNormalize(riderDto);
            var rider = await _repository.GetByUserIdAsync(userId);
            if (rider == null)
            {
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
                return;
            }

            await _repository.DeleteAsync(rider.Id);
        }

        private static void ValidateAndNormalize(RiderDTO riderDto)
        {
            Validator.ValidateObject(riderDto, new ValidationContext(riderDto), validateAllProperties: true);
            riderDto.CNPJ = BrazilianCnpj.Normalize(riderDto.CNPJ);
        }
    }
}
