using Moq;
using ProjectY.Shared.Pagination;
using RiderManager.DTOs;
using RiderManager.Managers;
using RiderManager.Services.MinioStorageService;
using RiderManager.Services.PreSignedService;
using RiderManager.Services.RiderServices;

namespace RiderManagerTests.Unit.Managers;

public sealed class RidersManagerTests
{
    [Fact]
    public async Task Listing_DoesNotRefreshPresignedUrlsPerRider()
    {
        var page = new CursorPage<RiderResponseDTO>(
            [
                CreateRider("rider-1"),
                CreateRider("rider-2")
            ],
            "next");
        var riderService = new Mock<IRiderService>();
        riderService.Setup(service => service.GetRidersAsync("cursor", 50)).ReturnsAsync(page);
        var storage = new Mock<IMinioFileStorageService>(MockBehavior.Strict);
        var presignedUrls = new Mock<IPresignedUrlService>(MockBehavior.Strict);
        var manager = new RidersManager(riderService.Object, storage.Object, presignedUrls.Object);

        var result = await manager.GetRidersAsync("cursor", 50);

        Assert.Same(page, result);
        storage.VerifyNoOtherCalls();
        presignedUrls.VerifyNoOtherCalls();
    }

    private static RiderResponseDTO CreateRider(string id) => new()
    {
        Id = id,
        UserId = id,
        Name = id,
        CNPJ = id,
        CNHNumber = id,
        CNHType = "A"
    };
}
