using RiderManager.Entities;

namespace RiderManager.Services.MinioStorageService
{
    public interface IMinioFileStorageService
    {
        Task<string> UploadFileAsync(IFormFile file, string userId, string? objectName = null);
        Task DeleteFileAsync(string objectName);
        Task<UploadFileEntity> GetPresignedUrlAsync(string objectName, string userId, int expirationInSeconds = 300);
    }
}
