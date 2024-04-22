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

        public async Task<(bool, UploadFileEntity?)> GetOrCreatePresignedUrlAsync(string riderId)
        {
            var rider = await _context.Riders
                .Include(r => r.CNHUrl)
                .FirstOrDefaultAsync(r => r.Id == riderId);

            if (rider?.CNHUrl != null)
            {
                UploadFileEntity uploadFileEntity = new UploadFileEntity()
                {
                    riderId = riderId,
                    expiryDate = rider.CNHUrl.Expiry,
                    fileName = rider.CNHUrl.ObjectName,
                    fileUrl = rider.CNHUrl.Url
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
            var rider = await _context.Riders.Include(r => r.CNHUrl).FirstOrDefaultAsync(r => r.UserId == uploadedFile.riderId);
            if (rider == null) throw new ArgumentException("Rider not found");

            if (rider.CNHUrl != null)
            {
                rider.CNHUrl.ObjectName = uploadedFile.fileName;
                rider.CNHUrl.Url = uploadedFile.fileUrl;
                rider.CNHUrl.Expiry = uploadedFile.expiryDate;
            }
            else
            {
                var presignedUrl = new PresignedUrl
                {
                    Id = Guid.NewGuid().ToString(),
                    ObjectName = uploadedFile.fileName,
                    Url = uploadedFile.fileUrl,
                    Expiry = uploadedFile.expiryDate,
                    RiderId = uploadedFile.riderId,
                    Rider = rider,
                };

                rider.CNHUrl = presignedUrl;
                _context.PresignedUrls.Add(presignedUrl);
            }

            await _context.SaveChangesAsync();
        }
    }
}
