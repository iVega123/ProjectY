using RiderManager.Entities;

namespace RiderManager.Services.PreSignedService
{
    public interface IPresignedUrlService
    {
        Task<(bool, UploadFileEntity?)> GetOrCreatePresignedUrlAsync(string riderId);

        Task StorePresignedUrlAsync(UploadFileEntity uploadedFile);
    }
}
