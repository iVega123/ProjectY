using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using RiderManager.Configurations;
using RiderManager.Entities;

namespace RiderManager.Services.MinioStorageService
{
    public class MinioFileStorageService : IMinioFileStorageService
    {
        private readonly IMinioClient _minioClient;
        private readonly IConfiguration _configuration;
        private readonly IMediaGuardClient _mediaGuard;

        public MinioFileStorageService(IMinioClient minioClient, IConfiguration configuration, IMediaGuardClient mediaGuard)
        {
            _minioClient = minioClient;
            _configuration = configuration;
            _mediaGuard = mediaGuard;
        }

        public async Task<string> UploadFileAsync(IFormFile file, string userId, string? objectName = null)
        {
            var bucketKey = _configuration.GetSection("MinIO").Get<MinIOOptions>()?.BucketName ?? throw new InvalidOperationException("JwtKey is not set in the configuration.");

            var sanitized = await _mediaGuard.SanitizeAsync(file);
            await EnsureBucketExistsAsync(bucketKey);
            var prefix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();
            // A queue retry has a stable upload identity; HTTP uploads use a random id.
            var component = objectName is null ? Guid.NewGuid().ToString("N")
                : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(objectName))).ToLowerInvariant();
            string uniqueFileName = $"riders/{prefix}/{component}.png";
            await PutAsync(bucketKey, uniqueFileName, sanitized.Image);
            await PutAsync(bucketKey, uniqueFileName + ".thumb.png", sanitized.Thumbnail);
            return uniqueFileName;
        }

        private async Task PutAsync(string bucket, string name, byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            await _minioClient.PutObjectAsync(new PutObjectArgs().WithBucket(bucket)
                .WithObject(name).WithStreamData(stream).WithObjectSize(bytes.Length).WithContentType("image/png"));
        }

        public async Task DeleteFileAsync(string objectName)
        {
            var bucket = _configuration.GetSection("MinIO").Get<MinIOOptions>()!.BucketName;
            await _minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(objectName));
            await _minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(objectName + ".thumb.png"));
        }

        private async Task EnsureBucketExistsAsync(string bucketKey)
        {

            var bucketArgs = new BucketExistsArgs()
                .WithBucket(bucketKey);

            bool found = await _minioClient.BucketExistsAsync(bucketArgs);
            if (!found)
            {
                var makeBucketArgs = new MakeBucketArgs()
                    .WithBucket(bucketKey);
                await _minioClient.MakeBucketAsync(makeBucketArgs);
            }
        }

        public async Task<UploadFileEntity> GetPresignedUrlAsync(string objectName, string userId, int expirationInSeconds = 300)
        {
            var bucketKey = _configuration.GetSection("MinIO").Get<MinIOOptions>()?.BucketName ?? throw new InvalidOperationException("bucketKey is not set in the configuration.");
            try
            {
                var args = new PresignedGetObjectArgs()
                    .WithBucket(bucketKey)
                    .WithObject(objectName)
                    .WithExpiry(expirationInSeconds);

                string url = await _minioClient.PresignedGetObjectAsync(args);
                return new UploadFileEntity
                {
                    ExpiryDate = DateTime.UtcNow.AddSeconds(expirationInSeconds),
                    FileName = objectName,
                    FileUrl = url,
                    UserId = userId
                };
            }
            catch (MinioException e)
            {
                Console.WriteLine("Error occurred: " + e.Message);
                throw new MinioException();
            }
        }
    }
}
