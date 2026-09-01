using RiderManager.DTOs;
using RiderManager.Services.MinioStorageService;
using RiderManager.Services.PreSignedService;
using RiderManager.Services.RiderServices;
using ProjectY.Shared.Pagination;

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
                var link = await _minioFileStorageService.GetPresignedUrlAsync(filePath, rider.UserId);
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

        public async Task UpdateRiderImageAsync(string userId, IFormFile cnhFile, string? objectName = null)
        {
            var filePath = await _minioFileStorageService.UploadFileAsync(cnhFile, objectName);
            var link = await _minioFileStorageService.GetPresignedUrlAsync(filePath, userId);
            await _preSignedUrlService.StorePresignedUrlAsync(link);
            return;
        }

        public Task<CursorPage<RiderResponseDTO>> GetRidersAsync(string? cursor, int? pageSize)
        {
            // Listing is a read-only database operation. URL refresh remains on the
            // single-rider path so a page never performs one object-storage call per row.
            return _riderService.GetRidersAsync(cursor, pageSize);
        }

        public async Task<RiderResponseDTO> GetRiderByUserIdAsync(string userId)
        {
            var rider = await _riderService.GetRiderByUserIdAsync(userId);

            var (isExpired, uploadFile) = await _preSignedUrlService.GetOrCreatePresignedUrlAsync(userId);
            if (!isExpired)
            {
                return await _riderService.GetRiderByUserIdAsync(userId);
            }

            if (uploadFile != null)
            {
                var link = await _minioFileStorageService.GetPresignedUrlAsync(uploadFile.FileName, userId);
                await _preSignedUrlService.StorePresignedUrlAsync(link);
                return await _riderService.GetRiderByUserIdAsync(userId);
            }

            return await _riderService.GetRiderByUserIdAsync(userId);

        }
    }
}
