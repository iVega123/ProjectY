using Microsoft.EntityFrameworkCore;
using RiderManager.Data;
using RiderManager.Entities;
using RiderManager.Models;
using RiderManager.Services.PreSignedService;

namespace RiderManagerTests.Unit.Services;

public sealed class PresignedUrlServiceTests
{
    [Fact]
    public async Task StoreAndRead_UsePublicUserIdButPersistInternalRiderForeignKey()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new ApplicationDbContext(options);
        context.Riders.Add(new Rider
        {
            Id = "internal-rider-id",
            UserId = "auth-user-id",
            Email = "rider@example.com",
            Name = "Rider",
            CNPJ = "92805586000180",
            CNHNumber = "12345678901",
            CNHType = "A"
        });
        await context.SaveChangesAsync();
        var service = new PresignedUrlService(context);
        var expiry = DateTime.UtcNow.AddHours(1);

        await service.StorePresignedUrlAsync(new UploadFileEntity
        {
            UserId = "auth-user-id",
            FileName = "cnh.png",
            FileUrl = "https://storage/cnh.png",
            ExpiryDate = expiry
        });

        var stored = await context.PresignedUrls.SingleAsync();
        Assert.Equal("internal-rider-id", stored.RiderId);

        var (isExpired, upload) = await service.GetOrCreatePresignedUrlAsync("auth-user-id");
        Assert.False(isExpired);
        Assert.NotNull(upload);
        Assert.Equal("auth-user-id", upload.UserId);
        Assert.Equal("cnh.png", upload.FileName);
    }
}
