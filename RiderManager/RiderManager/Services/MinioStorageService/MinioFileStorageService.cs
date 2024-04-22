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
        private readonly string[] permittedExtensions = { ".png", ".bmp" };
        private const long maxFileSize = 8 * 1024 * 1024; // 8 Megabytes

        public MinioFileStorageService(IMinioClient minioClient, IConfiguration configuration)
        {
            _minioClient = minioClient;
            _configuration = configuration;
        }

        public async Task<string> UploadFileAsync(IFormFile file)
        {
            var bucketKey = _configuration.GetSection("MinIO").Get<MinIOOptions>()?.BucketName ?? throw new InvalidOperationException("JwtKey is not set in the configuration.");

            await EnsureBucketExistsAsync(bucketKey);

            if (file.Length > maxFileSize)
            {
                throw new InvalidOperationException("File size exceeds the limit of 8MB.");
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!permittedExtensions.Contains(extension))
            {
                throw new InvalidOperationException("Invalid file type. Only PNG and BMP files are allowed.");
            }

            string uniqueFileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}";
            string contentType = file.ContentType;

            using (var fileStream = file.OpenReadStream())
            {
                var putObjectArgs = new PutObjectArgs()
                    .WithBucket(bucketKey)
                    .WithObject(uniqueFileName)
                    .WithStreamData(fileStream)
                    .WithObjectSize(file.Length)
                    .WithContentType(contentType);

                await _minioClient.PutObjectAsync(putObjectArgs);
            }

            return uniqueFileName;
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

        public async Task<UploadFileEntity> GetPresignedUrlAsync(string objectName, string riderId, int expirationInSeconds = 86400)
        {
            var bucketKey = _configuration.GetSection("MinIO").Get<MinIOOptions>()?.BucketName ?? throw new InvalidOperationException("bucketKey is not set in the configuration.");
            try
            {
                var args = new PresignedGetObjectArgs()
                    .WithBucket(bucketKey)
                    .WithObject(objectName)
                    .WithExpiry(expirationInSeconds);

                string url = await _minioClient.PresignedGetObjectAsync(args);
                return new UploadFileEntity() { expiryDate = DateTime.UtcNow.AddSeconds(expirationInSeconds), fileName = objectName, fileUrl = url, riderId = riderId };
            }
            catch (MinioException e)
            {
                Console.WriteLine("Error occurred: " + e.Message);
                throw new MinioException();
            }
        }
    }
}
