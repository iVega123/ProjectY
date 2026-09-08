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
    [Theory]
    [InlineData("database")]
    [InlineData("rider")]
    [InlineData("motorcycle")]
    [InlineData("insert")]
    public async Task CreateRental_OnlyPreflightFailuresPermitIdempotentRetry(string stage)
    {
        var failure = new TimeoutException("dependency timeout");
        var repository = new Mock<IRentalRepository>();
        repository.Setup(r => r.HasOverlappingRentalAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var riders = new Mock<IRiderProjectionStore>();
        riders.Setup(r => r.GetAsync("rider", It.IsAny<CancellationToken>())).ReturnsAsync(new RiderView("rider", true, 1, "Ada Lovelace"));
        var motorcycles = new Mock<IMotorcycleService>();
        motorcycles.Setup(m => m.GetMotorcycleByIdAsync("ABC1D23")).ReturnsAsync(new Motorcycle { id = Guid.NewGuid().ToString(), licensePlate = "ABC1D23" });
        if (stage == "database") repository.Setup(r => r.HasOverlappingRentalAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        if (stage == "rider") riders.Setup(r => r.GetAsync("rider", It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        if (stage == "motorcycle") motorcycles.Setup(m => m.GetMotorcycleByIdAsync("ABC1D23")).ThrowsAsync(failure);
        if (stage == "insert") repository.Setup(r => r.CreateRentalAsync(It.IsAny<RentalOperations.Model.Rental>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var service = new RentalService(repository.Object, Mock.Of<IMapper>(), riders.Object, motorcycles.Object);
        var request = new RentalCreateDto
        {
            MotocycleLicencePlate = "ABC1D23",
            StartDate = DateTime.UtcNow.AddDays(1),
            PredictedEndDate = DateTime.UtcNow.AddDays(8)
        };
        var error = await Record.ExceptionAsync(() => service.CreateRentalAsync(request, "rider"));
        if (stage is "insert") Assert.Same(failure, error);
        else
        {
            Assert.Same(failure, Assert.IsType<PreWriteDependencyException>(error).InnerException);
            repository.Verify(r => r.CreateRentalAsync(
                It.IsAny<RentalOperations.Model.Rental>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task CreateRental_WhenTheMotorcycleIsRetired_RejectsWithoutInsertingRental()
    {
        var repository = new Mock<IRentalRepository>();
        repository.Setup(candidate => candidate.HasOverlappingRentalAsync(
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var riders = new Mock<IRiderProjectionStore>();
        riders.Setup(service => service.GetAsync("rider-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RiderView("rider-1", true, 1, "Ada Lovelace"));
        var motorcycles = new Mock<IMotorcycleService>();
        motorcycles.Setup(service => service.GetMotorcycleByIdAsync("RET-0001"))
            .ReturnsAsync(new Motorcycle
            {
                id = Guid.NewGuid().ToString(),
                licensePlate = "RET-0001",
                model = "Retirement race",
                year = 2026,
                retiredAtUtc = DateTime.UtcNow
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
        repository.Verify(candidate => candidate.CreateRentalAsync(
            It.IsAny<RentalOperations.Model.Rental>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IsMotorcycleCurrentlyRented_NormalizesThePlateBeforeAsking()
    {
        var repository = new Mock<IRentalRepository>();
        repository.Setup(candidate => candidate.IsMotorcycleCurrentlyRentedAsync(
                "BUS0Y01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new RentalService(
            repository.Object,
            Mock.Of<IMapper>(),
            Mock.Of<IRiderProjectionStore>(),
            Mock.Of<IMotorcycleService>());

        Assert.True(await service.IsMotorcycleCurrentlyRentedAsync(" bus0y01 "));
    }
}
