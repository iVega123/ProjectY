using RiderManager.DTOs;
using RiderManager.Services.MinioStorageService;
using RiderManager.Services.PreSignedService;
using RiderManager.Services.RiderServices;

namespace RiderManager.Managers
{
    public class RidersManager : IRiderManager
    {
        private readonly IRiderService _riderService;
        private readonly IMinioFileStorageService _minioFileStorageService;
        private readonly IPresignedUrlService _preSignedUrlService;

        public RidersManager(IRiderService riderService, IMinioFileStorageService minioFileStorageService, IPresignedUrlService presignedUrlService)
        {
            _riderService = riderService;
            _minioFileStorageService = minioFileStorageService;
            _preSignedUrlService = presignedUrlService;
        }

        public async Task AddRiderAsync(RiderDTO riderDto)
        {
            if (riderDto.CNHImagePath != null)
            {
                var filePath = await _minioFileStorageService.UploadFileAsync(riderDto.CNHImagePath);
                var rider = await _riderService.AddRiderAsync(riderDto);
                var link = await _minioFileStorageService.GetPresignedUrlAsync(filePath, rider.Id);
                await _preSignedUrlService.StorePresignedUrlAsync(link);

                return;
            }
            await _riderService.AddRiderAsync(riderDto);

            return;
        }

        public async Task UpdateRiderAsync(string userId, RiderDTO riderDto)
        {
            if (riderDto.CNHImagePath != null)
            {
                var filePath = await _minioFileStorageService.UploadFileAsync(riderDto.CNHImagePath);
                await _riderService.UpdateRiderAsync(userId, riderDto);
                var link = await _minioFileStorageService.GetPresignedUrlAsync(filePath, userId);
                await _preSignedUrlService.StorePresignedUrlAsync(link);
            }
            else
            {
                await _riderService.UpdateRiderAsync(userId, riderDto);
            }
        }

        public async Task DeleteRiderAsync(string userId)
        {
            await _riderService.DeleteRiderAsync(userId);
        }

        public async Task UpdateRiderImageAsync(string userId, IFormFile cnhFile)
        {
            var filePath = await _minioFileStorageService.UploadFileAsync(cnhFile);
            var link = await _minioFileStorageService.GetPresignedUrlAsync(filePath, userId);
            await _preSignedUrlService.StorePresignedUrlAsync(link);
            return;
        }

        public async Task<IEnumerable<RiderResponseDTO>> GetAllRidersAsync()
        {
            var riders = await _riderService.GetAllRidersAsync();
            var riderDtos = new List<RiderResponseDTO>();

            foreach (var rider in riders)
            {
                var (isExpired, uploadFile) = await _preSignedUrlService.GetOrCreatePresignedUrlAsync(rider.Id);
                if (uploadFile != null)
                {
                    if (isExpired)
                    {
                        var link = await _minioFileStorageService.GetPresignedUrlAsync(uploadFile.fileName, uploadFile.riderId);
                        await _preSignedUrlService.StorePresignedUrlAsync(link);
                    }
                    rider.CNHUrl = uploadFile.fileUrl;
                    riderDtos.Add(rider);
                }
                else
                {
                    riderDtos.Add(rider);
                }
            }

            return riderDtos;
        }

        public async Task<RiderResponseDTO> GetRiderByUserIdAsync(string userId)
        {
            var rider = await _riderService.GetRiderByUserIdAsync(userId);

            var (isExpired, uploadFile) = await _preSignedUrlService.GetOrCreatePresignedUrlAsync(rider.Id);
            if (!isExpired)
            {
                return await _riderService.GetRiderByUserIdAsync(userId);
            }

            if(uploadFile != null)
            {
                var link = await _minioFileStorageService.GetPresignedUrlAsync(uploadFile.fileName, userId);
                await _preSignedUrlService.StorePresignedUrlAsync(link);
                return await _riderService.GetRiderByUserIdAsync(userId);
            }

            return await _riderService.GetRiderByUserIdAsync(userId);

        }
    }
}
