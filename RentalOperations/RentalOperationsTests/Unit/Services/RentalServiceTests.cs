using AutoMapper;
using Moq;
using RentalOperations.CrossCutting.Model;
using RentalOperations.CrossCutting.Services;
using RentalOperations.Domain;
using RentalOperations.DTOs;
using RentalOperations.Repository;
using RentalOperations.Services;

namespace RentalOperationsTests.Unit.Services;

public sealed class RentalServiceTests
{
    [Fact]
    public async Task CreateRental_WhenRetirementClaimWins_RejectsWithoutInsertingRental()
    {
        var repository = new Mock<IRentalRepository>();
        repository.Setup(candidate => candidate.HasOverlappingRentalAsync(
                "RET-0001",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>()))
            .ReturnsAsync(false);
        repository.Setup(candidate => candidate.TryClaimRentalAsync("RET-0001", It.IsAny<string>()))
            .ReturnsAsync(MotorcycleClaimResult.Retired);
        var riders = new Mock<IRiderManagerService>();
        riders.Setup(service => service.GetRiderByIdAsync("rider-1"))
            .ReturnsAsync(new Rider
            {
                Id = "rider-1",
                UserId = "rider-1",
                CNHType = "A"
            });
        var motorcycles = new Mock<IMotorcycleService>();
        motorcycles.Setup(service => service.GetMotorcycleByIdAsync("RET-0001"))
            .ReturnsAsync(new Motorcycle
            {
                licensePlate = "RET-0001",
                model = "Retirement race",
                year = 2026
            });
        var service = new RentalService(
            repository.Object,
            Mock.Of<IMapper>(),
            riders.Object,
            motorcycles.Object);
        var request = new RentalCreateDto
        {
            MotocycleLicencePlate = " ret-0001 ",
            StartDate = DateTime.UtcNow.Date.AddDays(1),
            PredictedEndDate = DateTime.UtcNow.Date.AddDays(8)
        };

        await Assert.ThrowsAsync<MotorcycleRetiredException>(() =>
            service.CreateRentalAsync(request, "rider-1"));
        Assert.Equal("RET-0001", request.MotocycleLicencePlate);
        repository.Verify(candidate => candidate.CreateRentalAsync(It.IsAny<RentalOperations.Model.Rental>()), Times.Never);
    }

    [Fact]
    public async Task TryRetireMotorcycle_WhenActiveRentalClaimExists_ReturnsFalse()
    {
        var repository = new Mock<IRentalRepository>();
        repository.Setup(candidate => candidate.TryClaimRetirementAsync("BUSY-0001"))
            .ReturnsAsync(MotorcycleClaimResult.ActiveRental);
        var service = new RentalService(
            repository.Object,
            Mock.Of<IMapper>(),
            Mock.Of<IRiderManagerService>(),
            Mock.Of<IMotorcycleService>());

        Assert.False(await service.TryRetireMotorcycleAsync(" busy-0001 "));
    }

    [Fact]
    public async Task TryReserveLicensePlateRename_NormalizesBothClaimKeys()
    {
        var repository = new Mock<IRentalRepository>();
        repository.Setup(candidate => candidate.TryReserveLicensePlateRenameAsync(
                "OLD-0001",
                "NEW-0001"))
            .ReturnsAsync(true);
        var service = new RentalService(
            repository.Object,
            Mock.Of<IMapper>(),
            Mock.Of<IRiderManagerService>(),
            Mock.Of<IMotorcycleService>());

        Assert.True(await service.TryReserveLicensePlateRenameAsync(
            " old-0001 ",
            " new-0001 "));
    }
}
