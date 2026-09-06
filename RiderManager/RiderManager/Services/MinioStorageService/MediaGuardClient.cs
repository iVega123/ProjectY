using System.Net;

namespace RiderManager.Services.MinioStorageService;

public sealed record SanitizedImage(byte[] Image, byte[] Thumbnail);

public interface IMediaGuardClient
{
    Task<SanitizedImage> SanitizeAsync(IFormFile file);
}

public sealed class MediaGuardClient(HttpClient client) : IMediaGuardClient
{
    private sealed record WireImage(string Image, string Thumbnail, string ContentType);

    public async Task<SanitizedImage> SanitizeAsync(IFormFile file)
    {
        if (file.Length is <= 0 or > 8 * 1024 * 1024)
            throw new InvalidDataException("Image must contain between 1 byte and 8 MiB.");
        await using var stream = file.OpenReadStream();
        using var content = new StreamContent(stream);
        using var response = await client.PostAsync("/sanitize", content);
        if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge)
            throw new InvalidDataException("Invalid image content or dimensions.");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<WireImage>()
            ?? throw new InvalidDataException("Missing sanitized image.");
        if (result.ContentType != "image/png") throw new InvalidDataException("Unexpected sanitized image type.");
        return new(Convert.FromBase64String(result.Image), Convert.FromBase64String(result.Thumbnail));
    }
}
