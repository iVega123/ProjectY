using AutoMapper;
using Google.Protobuf;
using Moq;
using ProjectY.Events;
using RentalOperations.CrossCutting.Services;
using RentalOperations.Domain;
using RentalOperations.DTOs;
using RentalOperations.Repository;
using RentalOperations.Services;
using Xunit;

namespace RentalOperationsTests.Unit.Projections;

public sealed class RiderProjectionTests
{
    private static RentalCreateDto Request() => new()
    {
        MotorcycleId = Guid.NewGuid(),
        StartDate = DateTime.UtcNow.Date.AddDays(1),
        PredictedEndDate = DateTime.UtcNow.Date.AddDays(8)
    };

    private static Mock<IRentalRepository> ReadyRepository()
    {
        var repository = new Mock<IRentalRepository>();
        repository.Setup(r => r.HasOverlappingRentalAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return repository;
    }

    // The refactor this guards against is the tempting one: reaching for identity
    // while assembling the rental. There is no seam left to do it through, and this
    // asserts that rather than trusting it.
    [Fact]
    public void RentalCreationHasNoNetworkSeamToIdentity()
    {
        var dependencies = typeof(RentalService).GetConstructors().Single()
            .GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Contains(typeof(IRiderProjectionStore), dependencies);
        Assert.DoesNotContain(dependencies, type => type.Name.Contains("RiderManager", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies, type => type == typeof(IHttpClientFactory));
        Assert.Null(typeof(RentalService).Assembly.GetType("RentalOperations.CrossCutting.Services.IRiderManagerService"));
    }

    // A projection that lags refuses the rental, and says which kind of "no" it is.
    // "Awaiting processing" is retryable; "not entitled" is not. Collapsing the two
    // into one denial is what makes a lagging consumer look like a rejected rider.
    [Fact]
    public async Task LaggingProjectionRefusesWithAnExplicitAwaitingAnswer()
    {
        var riders = new Mock<IRiderProjectionStore>();
        riders.Setup(r => r.GetAsync("rider", It.IsAny<CancellationToken>())).ReturnsAsync((RiderView?)null);
        var service = new RentalService(ReadyRepository().Object, Mock.Of<IMapper>(),
            riders.Object, Mock.Of<IMotorcycleService>());

        var pending = await Assert.ThrowsAsync<RiderProjectionPendingException>(
            () => service.CreateRentalAsync(Request(), "rider"));
        Assert.Contains("awaiting processing", pending.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnverifiedRiderIsRefusedPermanently()
    {
        var riders = new Mock<IRiderProjectionStore>();
        riders.Setup(r => r.GetAsync("rider", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RiderView("rider", false, 1, "Ada Lovelace"));
        var service = new RentalService(ReadyRepository().Object, Mock.Of<IMapper>(),
            riders.Object, Mock.Of<IMotorcycleService>());

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRentalAsync(Request(), "rider"));
    }

    // Field numbers are shared with rider.proto, so v1 consumers that have not moved
    // read a v2 payload and skip the name. This is what makes the rollout cheap, and
    // it is the reason a new subject was cheaper than evolving the governed one.
    [Fact]
    public void V2PayloadStaysReadableByTheV1Decoder()
    {
        var v2 = new RiderEventV2
        {
            EventId = "e1",
            RiderId = "rider-1",
            OccurredAtMs = 1700000000000,
            Verified = true,
            Name = "Ada Lovelace"
        };

        var v1 = RiderEvent.Parser.ParseFrom(v2.ToByteArray());

        Assert.Equal("rider-1", v1.RiderId);
        Assert.True(v1.Verified);
        Assert.Equal(1700000000000, v1.OccurredAtMs);
    }
}
