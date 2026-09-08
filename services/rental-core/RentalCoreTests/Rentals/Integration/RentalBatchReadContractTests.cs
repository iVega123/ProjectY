using System.Net;
using System.Net.Http.Json;
using RentalOperations.DTOs;
using RentalOperations.Model;
using RentalOperations.Services;

namespace RentalOperationsTests.Integration;

/// <summary>
/// O contrato de leitura em lote do #138.
///
/// A issue pede que remover um endpoint de lote deixe um teste vermelho antes de
/// deixar uma tela em branco. É isto: a rota existe, respeita o teto, e nunca
/// devolve o aluguel de outro piloto. O armazenamento tem os seus próprios
/// testes contra o schema real -- aqui o assunto é a borda.
/// </summary>
public class RentalBatchReadContractTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Batch_ReturnsTheRequestedRentalsOfTheCaller()
    {
        var first = Seed("rider-batch-a");
        var second = Seed("rider-batch-a");
        using var client = factory.CreateAuthenticatedClient("Rider", "rider-batch-a");

        var response = await client.GetAsync(Batch(first.Id, second.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rentals = await response.Content.ReadFromJsonAsync<List<ResponseRentalDTO>>();
        Assert.NotNull(rentals);
        Assert.Equal(
            new[] { first.Id.ToString(), second.Id.ToString() }.OrderBy(id => id),
            rentals!.Select(rental => rental.RentalId).OrderBy(id => id));
    }

    [Fact]
    public async Task Batch_NeverReturnsAnotherRidersRental()
    {
        var mine = Seed("rider-batch-b");
        var theirs = Seed("rider-batch-c");
        using var client = factory.CreateAuthenticatedClient("Rider", "rider-batch-b");

        var response = await client.GetAsync(Batch(mine.Id, theirs.Id));

        // 200 com o alheio ausente, e não 403: recusar diria "este id existe, mas
        // não é seu", que é exatamente o que alguém varrendo ids quer ouvir.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rentals = await response.Content.ReadFromJsonAsync<List<ResponseRentalDTO>>();
        Assert.Equal(mine.Id.ToString(), Assert.Single(rentals!).RentalId);
    }

    [Fact]
    public async Task Batch_ForAnAdmin_ReturnsEveryRequestedRental()
    {
        var one = Seed("rider-batch-d");
        var another = Seed("rider-batch-e");
        using var client = factory.CreateAuthenticatedClient("Admin", "an-admin");

        var response = await client.GetAsync(Batch(one.Id, another.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rentals = await response.Content.ReadFromJsonAsync<List<ResponseRentalDTO>>();
        Assert.Equal(2, rentals!.Count);
    }

    [Fact]
    public async Task Batch_AboveTheCap_IsRefusedRatherThanServed()
    {
        using var client = factory.CreateAuthenticatedClient("Rider", "rider-batch-f");
        var ids = Enumerable.Range(0, RentalService.MaxBatchSize + 1).Select(_ => Guid.NewGuid());

        var response = await client.GetAsync("/api/Rental/batch?ids=" + string.Join(',', ids));

        // Um lote sem teto é uma consulta arbitrária escrita pelo cliente.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task Batch_WithoutUsableIds_IsRefused(string ids)
    {
        using var client = factory.CreateAuthenticatedClient("Rider", "rider-batch-g");

        var response = await client.GetAsync("/api/Rental/batch?ids=" + ids);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Batch_WithoutGatewayIdentity_IsUnauthorized()
    {
        using var client = factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync("/api/Rental/batch?ids=" + Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string Batch(params Guid[] ids) =>
        "/api/Rental/batch?ids=" + string.Join(',', ids);

    private Rental Seed(string riderId) => factory.Repository.SeedRental(new Rental
    {
        MotorcycleId = Guid.NewGuid(),
        UserId = riderId,
        StartDate = DateTime.UtcNow.Date,
        PredictedEndDate = DateTime.UtcNow.Date.AddDays(7),
        InitCost = 210m
    });
}
