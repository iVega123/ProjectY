using AutoMapper;
using Moq;
using RentalCore.Errors;
using RentalOperations.Domain;
using RentalOperations.DTOs;
using RentalOperations.Model;
using RentalOperations.Repository;
using RentalOperations.Services;

namespace RentalOperationsTests.Unit.Services;

public sealed class RentalServiceTests
{
    private static readonly Guid Motorcycle = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static RentalCreateDto Request() => new()
    {
        MotorcycleId = Motorcycle,
        StartDate = DateTime.UtcNow.Date.AddDays(1),
        PredictedEndDate = DateTime.UtcNow.Date.AddDays(8)
    };

    private static Mock<IRentalRepository> Repository(
        MotorcycleAvailability motorcycle = MotorcycleAvailability.Available,
        bool overlaps = false)
    {
        var repository = new Mock<IRentalRepository>();
        repository.Setup(r => r.ReadCreationPreconditionsAsync(
                It.IsAny<string>(), Motorcycle, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string rider, Guid _, DateTime _, DateTime _, CancellationToken _) =>
                new RentalPreconditions(new RiderView(rider, true, 1, "Ada Lovelace"), motorcycle, overlaps));
        return repository;
    }

    [Theory]
    [InlineData("preconditions")]
    [InlineData("insert")]
    public async Task CreateRental_OnlyPreflightFailuresPermitIdempotentRetry(string stage)
    {
        var failure = new TimeoutException("dependency timeout");
        var repository = Repository();
        if (stage == "preconditions") repository.Setup(r => r.ReadCreationPreconditionsAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        if (stage == "insert") repository.Setup(r => r.CreateRentalAsync(It.IsAny<Rental>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var service = new RentalService(repository.Object, Mock.Of<IMapper>());
        var error = await Record.ExceptionAsync(() => service.CreateRentalAsync(Request(), "rider"));
        if (stage is "insert") Assert.Same(failure, error);
        else
        {
            Assert.Same(failure, Assert.IsType<PreWriteDependencyException>(error).InnerException);
            repository.Verify(r => r.CreateRentalAsync(It.IsAny<Rental>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task CreateRental_WhenTheMotorcycleIsRetired_RejectsWithoutInsertingRental()
    {
        var repository = Repository(MotorcycleAvailability.Retired);
        var service = new RentalService(repository.Object, Mock.Of<IMapper>());

        await Assert.ThrowsAsync<MotorcycleRetiredException>(() =>
            service.CreateRentalAsync(Request(), "rider-1"));
        repository.Verify(candidate => candidate.CreateRentalAsync(
            It.IsAny<Rental>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateRental_WhenTheMotorcycleDoesNotExist_RejectsWithoutInsertingRental()
    {
        var repository = Repository(MotorcycleAvailability.Missing);
        var service = new RentalService(repository.Object, Mock.Of<IMapper>());

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            service.CreateRentalAsync(Request(), "rider-1"));
        repository.Verify(candidate => candidate.CreateRentalAsync(
            It.IsAny<Rental>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateRental_WhenTheScheduleOverlaps_RejectsWithoutInsertingRental()
    {
        var repository = Repository(overlaps: true);
        var service = new RentalService(repository.Object, Mock.Of<IMapper>());

        await Assert.ThrowsAsync<ActiveRentalConflictException>(() =>
            service.CreateRentalAsync(Request(), "rider-1"));
        repository.Verify(candidate => candidate.CreateRentalAsync(
            It.IsAny<Rental>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateRental_WritesTheProjectedRiderName()
    {
        Rental? written = null;
        var repository = Repository();
        repository.Setup(r => r.CreateRentalAsync(It.IsAny<Rental>(), It.IsAny<CancellationToken>()))
            .Callback((Rental rental, CancellationToken _) => written = rental)
            .ReturnsAsync((Rental rental, CancellationToken _) => rental);
        var service = new RentalService(repository.Object, Mock.Of<IMapper>());

        await service.CreateRentalAsync(Request(), "rider-1");

        Assert.Equal("Ada Lovelace", written!.RiderName);
        Assert.Equal("rider-1", written.UserId);
    }

    // Havia aqui um teste de normalização de placa -- " bus0y01 " tinha de virar
    // "BUS0Y01" antes de chegar ao banco. Ele sai porque o problema saiu: a
    // referência é um Guid, e um Guid não tem espaço em branco, caixa, hífen nem
    // formato regional para alguém normalizar errado. A placa continua validada
    // onde ela ainda é entrada de usuário, no cadastro da moto.

    [Fact]
    public async Task RentalsByIds_AreFilteredToTheCaller()
    {
        var mine = new Rental { MotorcycleId = Motorcycle, UserId = "rider-1", Id = Guid.NewGuid() };
        var theirs = new Rental { MotorcycleId = Motorcycle, UserId = "rider-2", Id = Guid.NewGuid() };
        var repository = new Mock<IRentalRepository>();
        repository.Setup(r => r.GetRentalsByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([mine, theirs]);
        var service = new RentalService(repository.Object, ProjectingMapper());

        var visible = await service.GetRentalsByIdsAsync([mine.Id, theirs.Id], "rider-1", isAdmin: false);

        // Pedir o id de outro não é erro, é ausência. Um 403 aqui diria "existe,
        // mas não é seu" -- e isso é um oráculo de existência para quem varrer ids.
        Assert.Equal(mine.Id.ToString(), Assert.Single(visible).RentalId);
    }

    [Fact]
    public async Task RentalsByIds_AreNotFilteredForAnAdmin()
    {
        var mine = new Rental { MotorcycleId = Motorcycle, UserId = "rider-1", Id = Guid.NewGuid() };
        var theirs = new Rental { MotorcycleId = Motorcycle, UserId = "rider-2", Id = Guid.NewGuid() };
        var repository = new Mock<IRentalRepository>();
        repository.Setup(r => r.GetRentalsByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([mine, theirs]);
        var service = new RentalService(repository.Object, ProjectingMapper());

        var visible = await service.GetRentalsByIdsAsync([mine.Id, theirs.Id], "an-admin", isAdmin: true);

        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public async Task IsMotorcycleCurrentlyRented_AsksByIdentity()
    {
        var repository = new Mock<IRentalRepository>();
        repository.Setup(candidate => candidate.IsMotorcycleCurrentlyRentedAsync(
                Motorcycle, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new RentalService(repository.Object, Mock.Of<IMapper>());

        Assert.True(await service.IsMotorcycleCurrentlyRentedAsync(Motorcycle));
    }

    /// <summary>
    /// Fechar grava a data e o estado, e mais nada.
    ///
    /// A devolução é dois dias atrasada de propósito: era o caso em que a versão
    /// anterior somava R$ 50 por dia e escrevia a frase que explicava a soma,
    /// tudo dentro de um método chamado Calculate. O aluguel que chega ao
    /// repositório agora não carrega número nenhum além do combinado.
    /// </summary>
    [Fact]
    public async Task CloseRental_RecordsTheDateAndStatusWithoutSettling()
    {
        var start = DateTime.UtcNow.Date;
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            MotorcycleId = Motorcycle,
            UserId = "rider-1",
            StartDate = start,
            PredictedEndDate = start.AddDays(7),
            InitCost = 210m
        };
        var repository = new Mock<IRentalRepository>();
        repository.Setup(r => r.GetRentalByIdAsync(rental.Id.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rental);
        Rental? persisted = null;
        repository.Setup(r => r.UpdateRentalAsync(
                It.IsAny<Rental>(), It.IsAny<CancellationToken>()))
            .Callback((Rental updated, CancellationToken _) => persisted = updated)
            .Returns(Task.CompletedTask);
        var service = new RentalService(repository.Object, Mock.Of<IMapper>());

        await service.CloseRentalAsync(rental.Id.ToString(), "rider-1", start.AddDays(9));

        Assert.NotNull(persisted);
        Assert.Equal(start.AddDays(9), persisted!.EndDate);
        Assert.Equal(RentalStatus.Completed, persisted.Status);
        Assert.Equal(210m, persisted.InitCost);
    }

    /// <summary>
    /// Uma devolução anterior ao início não existe, e o billing não teria como
    /// recusá-la: para ele seriam zero dias usados e o plano inteiro em multa --
    /// a fatura mais cara possível, a partir de uma data impossível.
    /// </summary>
    [Fact]
    public async Task CloseRental_BeforeTheRentalStarted_IsRefusedWithoutWriting()
    {
        var start = DateTime.UtcNow.Date;
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            MotorcycleId = Motorcycle,
            UserId = "rider-1",
            StartDate = start,
            PredictedEndDate = start.AddDays(7),
            InitCost = 210m
        };
        var repository = new Mock<IRentalRepository>();
        repository.Setup(r => r.GetRentalByIdAsync(rental.Id.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rental);
        var service = new RentalService(repository.Object, Mock.Of<IMapper>());

        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            service.CloseRentalAsync(rental.Id.ToString(), "rider-1", start.AddDays(-1)));
        repository.Verify(r => r.UpdateRentalAsync(
            It.IsAny<Rental>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// O suficiente para o filtro ser observável. Estes testes são sobre quem
    /// pode ver o quê, não sobre o mapeamento -- que tem o seu próprio caminho.
    /// </summary>
    private static IMapper ProjectingMapper()
    {
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<IReadOnlyList<ResponseRentalDTO>>(It.IsAny<object>()))
            .Returns((object source) => ((IEnumerable<Rental>)source)
                .Select(rental => new ResponseRentalDTO
                {
                    RentalId = rental.Id.ToString(),
                    UserId = rental.UserId
                })
                .ToList());
        return mapper.Object;
    }
}
