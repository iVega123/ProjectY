using System.Net;
using Microsoft.AspNetCore.Http;
using RiderManager.Services.MinioStorageService;

namespace RiderManagerTests.Unit.Services;

public sealed class MediaGuardClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, typeof(InvalidDataException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, typeof(HttpRequestException))]
    public async Task RefusesRejectedOrUnavailableSanitizer(HttpStatusCode status, Type error)
    {
        using var http = new HttpClient(new Handler(status)) { BaseAddress = new Uri("http://media-guard") };
        var client = new MediaGuardClient(http);
        using var bytes = new MemoryStream("<html>bad</html>"u8.ToArray());
        var file = new FormFile(bytes, 0, bytes.Length, "image", "renamed.png");
        var exception = await Record.ExceptionAsync(() => client.SanitizeAsync(file));
        Assert.IsType(error, exception);
    }

    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}
