using AutoMapper;
using Moq;
using ProjectY.Shared.Pagination;
using RiderManager.DTOs;
using RiderManager.Models;
using RiderManager.Repositories;
using RiderManager.Services.RiderServices;

namespace RiderManagerTests.Unit.Services;

public sealed class RiderServiceTests
{
    [Fact]
    public async Task GetRidersAsync_OmitsExpiredCnhUrlsWithoutRefreshingStorage()
    {
        var expired = CreateRider("expired", DateTime.UtcNow.AddMinutes(-1));
        var valid = CreateRider("valid", DateTime.UtcNow.AddMinutes(10));
        var page = new CursorPage<Rider>([expired, valid], "next");
        var mapped = new List<RiderResponseDTO>
        {
            CreateResponse(expired, expired.CNHUrl!.Url),
            CreateResponse(valid, valid.CNHUrl!.Url)
        };
        var repository = new Mock<IRiderRepository>();
        repository.Setup(instance => instance.GetPageAsync("cursor", 25)).ReturnsAsync(page);
        var mapper = new Mock<IMapper>();
        mapper.Setup(instance => instance.Map<IReadOnlyList<RiderResponseDTO>>(page.Items))
            .Returns(mapped);
        var service = new RiderService(repository.Object, mapper.Object);

        var result = await service.GetRidersAsync("cursor", 25);

        Assert.Null(result.Items[0].CNHUrl);
        Assert.Equal(valid.CNHUrl.Url, result.Items[1].CNHUrl);
        Assert.Equal("next", result.NextCursor);
        repository.Verify(instance => instance.GetPageAsync("cursor", 25), Times.Once);
    }

    private static Rider CreateRider(string id, DateTime expiry) => new()
    {
        Id = id,
        UserId = $"user-{id}",
        Email = $"{id}@example.test",
        Name = id,
        CNPJ = "92805586000180",
        DateOfBirth = new DateTime(1990, 1, 1),
        CNHNumber = "12345678901",
        CNHType = "A",
        CNHUrl = new PresignedUrl
        {
            Url = $"https://storage.example.test/{id}",
            ObjectName = id,
            Expiry = expiry
        }
    };

    private static RiderResponseDTO CreateResponse(Rider rider, string? url) => new()
    {
        Id = rider.Id,
        UserId = rider.UserId,
        Name = rider.Name,
        CNPJ = rider.CNPJ,
        DateOfBirth = rider.DateOfBirth,
        CNHNumber = rider.CNHNumber,
        CNHType = rider.CNHType,
        CNHUrl = url
    };
}
