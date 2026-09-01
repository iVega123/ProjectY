using RiderManager.Entities;

namespace RiderManager.Services.MinioStorageService
{
    public interface IMinioFileStorageService
    {
        Task<string> UploadFileAsync(IFormFile file, string? objectName = null);
        Task<UploadFileEntity> GetPresignedUrlAsync(string objectName, string userId, int expirationInSeconds = 86400);
    }
}
