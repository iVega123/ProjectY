using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MotoHub.Data;
using MotoHub.Models;
using MotoHub.Services;
using RentalCoreTests;

namespace MotoHubTests.Integration;

/// <summary>
/// O lote de motos, contra a declaração do console.
///
/// O aluguel referencia a moto pelo id desde o #134 e carrega apenas a placa;
/// modelo e ano moram aqui. Compor uma página de aluguéis sem este lote custa
/// uma requisição por linha -- o N+1 sai do navegador e entra no BFF, que é o
/// que o ADR 0014 recusa.
///
/// Os nomes dos campos vêm de <c>contracts/reads.json</c>, e não desta classe.
/// Renomear um campo aqui deixa este teste vermelho sem que ninguém precise
/// lembrar de atualizá-lo.
/// </summary>
public class MotorcycleBatchReadContractTests : IClassFixture<CustomWebApplicationFactory<Program>>
{
    private readonly CustomWebApplicationFactory<Program> _factory;
    private static readonly ReadContract.Read Declared =
        ReadContract.For("rental-core", "/api/motorcycles/batch");

    public MotorcycleBatchReadContractTests(CustomWebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Batch_AnswersWithEveryFieldTheConsoleDeclared()
    {
        var first = Seed("Honda CG 160", 2024);
        var second = Seed("Yamaha Factor", 2023);
        using var client = Rider();

        var response = await client.GetAsync(Batch(first, second));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var motorcycles = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, motorcycles.GetArrayLength());
        foreach (var motorcycle in motorcycles.EnumerateArray())
        {
            foreach (var field in Declared.Fields)
            {
                Assert.True(
                    motorcycle.TryGetProperty(field, out _),
                    $"contracts/reads.json declares '{field}', and the batch did not answer with it.");
            }
        }
    }

    /// <summary>
    /// Ler UMA moto já é do piloto que vai alugá-la, e um lote de ids é N vezes
    /// essa mesma leitura. O catálogo inteiro é que continua sendo inventário
    /// da frota -- essa fronteira tem teste próprio.
    /// </summary>
    [Fact]
    public async Task Batch_IsNotAnAdministratorRoute()
    {
        var motorcycle = Seed("Honda Biz", 2022);
        using var rider = Rider();

        var response = await rider.GetAsync(Batch(motorcycle));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Uma moto aposentada some da paginação e continua respondendo aqui: um
    /// aluguel antigo aponta para ela, e omiti-la deixaria a linha do histórico
    /// sem modelo nem placa.
    /// </summary>
    [Fact]
    public async Task Batch_StillResolvesARetiredMotorcycle()
    {
        var retired = Seed("Honda Pop", 2015, retired: true);
        using var client = Rider();

        var response = await client.GetAsync(Batch(retired));

        var motorcycles = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, motorcycles.GetArrayLength());
    }

    /// <summary>
    /// Um id ausente vira ausência na lista, e não 404: uma tela compõe o que
    /// chegou, e uma moto que não existe mais não pode apagar as outras linhas.
    /// </summary>
    [Fact]
    public async Task Batch_OmitsWhatItCannotFindInsteadOfFailing()
    {
        var known = Seed("Honda Titan", 2021);
        using var client = Rider();

        var response = await client.GetAsync(Batch(known, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var motorcycles = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, motorcycles.GetArrayLength());
    }

    [Fact]
    public async Task Batch_AboveTheDeclaredCap_IsRefusedRatherThanServed()
    {
        using var client = Rider();
        var ids = Enumerable.Range(0, Declared.MaxBatch!.Value + 1).Select(_ => Guid.NewGuid());

        var response = await client.GetAsync("/api/motorcycles/batch?ids=" + string.Join(',', ids));

        // Um lote sem teto é uma consulta arbitrária escrita pelo cliente.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MotorcycleService.MaxBatchSize, Declared.MaxBatch!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task Batch_WithoutUsableIds_IsRefused(string ids)
    {
        using var client = Rider();

        var response = await client.GetAsync("/api/motorcycles/batch?ids=" + ids);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Batch_WithoutGatewayIdentity_IsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/motorcycles/batch?ids=" + Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient Rider()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-rider");
        return client;
    }

    private static string Batch(params Guid[] ids) =>
        "/api/motorcycles/batch?" + Declared.IdsParameter + "=" + string.Join(',', ids);

    private Guid Seed(string model, int year, bool retired = false)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var motorcycle = new Motorcycle
        {
            LicensePlate = $"B{Random.Shared.Next(100000, 999999)}",
            Model = model,
            Year = year,
            RegistrationDate = DateTime.UtcNow,
            RetiredAtUtc = retired ? DateTime.UtcNow : null,
            RetirementReason = retired ? "sold" : null
        };
        context.Motorcycles.Add(motorcycle);
        context.SaveChanges();
        return motorcycle.Id;
    }
}
