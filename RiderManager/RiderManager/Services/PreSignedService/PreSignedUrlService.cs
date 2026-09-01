using Microsoft.EntityFrameworkCore;
using RiderManager.Data;
using RiderManager.Entities;
using RiderManager.Models;

namespace RiderManager.Services.PreSignedService
{
    public class PresignedUrlService : IPresignedUrlService
    {
        private readonly ApplicationDbContext _context;

        public PresignedUrlService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<(bool, UploadFileEntity?)> GetOrCreatePresignedUrlAsync(string userId)
        {
            var rider = await _context.Riders
                .Include(r => r.CNHUrl)
                .FirstOrDefaultAsync(r => r.UserId == userId);

            if (rider?.CNHUrl != null)
            {
                UploadFileEntity uploadFileEntity = new UploadFileEntity()
                {
                    UserId = userId,
                    ExpiryDate = rider.CNHUrl.Expiry,
                    FileName = rider.CNHUrl.ObjectName,
                    FileUrl = rider.CNHUrl.Url
                };

                if (rider.CNHUrl.Expiry > DateTime.UtcNow)
                {
                    return (false, uploadFileEntity);
                }
                return (true, uploadFileEntity);
            }
            return (true, null);
        }

        public async Task StorePresignedUrlAsync(UploadFileEntity uploadedFile)
        {
            var rider = await _context.Riders.Include(r => r.CNHUrl).FirstOrDefaultAsync(r => r.UserId == uploadedFile.UserId);
            if (rider == null) throw new ArgumentException("Rider not found");

            if (rider.CNHUrl != null)
            {
                rider.CNHUrl.ObjectName = uploadedFile.FileName;
                rider.CNHUrl.Url = uploadedFile.FileUrl;
                rider.CNHUrl.Expiry = uploadedFile.ExpiryDate;
            }
            else
            {
                var presignedUrl = new PresignedUrl
                {
                    Id = Guid.NewGuid().ToString(),
                    ObjectName = uploadedFile.FileName,
                    Url = uploadedFile.FileUrl,
                    Expiry = uploadedFile.ExpiryDate,
                    RiderId = rider.Id,
                    Rider = rider,
                };

                rider.CNHUrl = presignedUrl;
                _context.PresignedUrls.Add(presignedUrl);
            }

            await _context.SaveChangesAsync();
        }
    }
}
