namespace AuthGate.Services.File
{
    public class FileValidationService : IFileValidationService
    {
        private readonly string[] _permittedExtensions = { ".png", ".bmp" };
        private const long _maxFileSize = 8 * 1024 * 1024;

        public async Task<(Stream, string)> ValidateAndConvertFileAsync(IFormFile file)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            if (!_permittedExtensions.Contains(extension))
                throw new InvalidOperationException("Unsupported file type.");

            if (file.Length > _maxFileSize)
                throw new InvalidOperationException("File size exceeds the maximum allowed limit.");

            return (await ConvertToFileStreamAsync(file), extension);
        }

        private async Task<Stream> ConvertToFileStreamAsync(IFormFile file)
        {
            var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;
            return memoryStream;
        }
    }
}
