namespace AuthGate.Services.File
{
    public interface IFileValidationService
    {
        Task<(Stream, string)> ValidateAndConvertFileAsync(IFormFile file);
    }
}
