using RiderManager.Entities;

namespace RiderManager.Services.MinioStorageService
{
    public interface IMinioFileStorageService
    {
        Task<string> UploadFileAsync(IFormFile file);
        Task<UploadFileEntity> GetPresignedUrlAsync(string objectName, string riderId, int expirationInSeconds = 86400);
    }
}
