using Moq;
using Microsoft.AspNetCore.Http;
using ProjectY.Shared.Pagination;
using RiderManager.DTOs;
using RiderManager.Entities;
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

    [Fact]
    public async Task AddWithImage_UsesPublicUserIdThroughoutStorageFlow()
    {
        const string userId = "auth-user-id";
        var dto = new RiderDTO
        {
            UserId = userId,
            Name = "Rider",
            Email = "rider@example.com",
            CNPJ = "92805586000180",
            CNHNumber = "12345678901",
            CNHType = "A",
            CNHImagePath = Mock.Of<IFormFile>()
        };
        var riderService = new Mock<IRiderService>();
        riderService.Setup(service => service.AddRiderAsync(dto)).ReturnsAsync(new RiderResponseDTO
        {
            Id = "internal-rider-id",
            UserId = userId,
            Name = dto.Name,
            CNPJ = dto.CNPJ,
            CNHNumber = dto.CNHNumber,
            CNHType = dto.CNHType
        });
        var storage = new Mock<IMinioFileStorageService>();
        storage.Setup(service => service.UploadFileAsync(dto.CNHImagePath, null)).ReturnsAsync("cnh.png");
        storage.Setup(service => service.GetPresignedUrlAsync("cnh.png", userId, 86400))
            .ReturnsAsync(new UploadFileEntity
            {
                UserId = userId,
                FileName = "cnh.png",
                FileUrl = "https://storage/cnh.png",
                ExpiryDate = DateTime.UtcNow.AddHours(1)
            });
        var presignedUrls = new Mock<IPresignedUrlService>();
        var manager = new RidersManager(riderService.Object, storage.Object, presignedUrls.Object);

        await manager.AddRiderAsync(dto);

        storage.Verify(service => service.GetPresignedUrlAsync("cnh.png", userId, 86400), Times.Once);
        presignedUrls.Verify(service => service.StorePresignedUrlAsync(
            It.Is<UploadFileEntity>(file => file.UserId == userId)), Times.Once);
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
