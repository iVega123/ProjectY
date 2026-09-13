using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalOperations.Model;
using RentalOperations.Repository;
using ProjectY.Shared.Pagination;

namespace RentalOperationsTests.Integration;

/// <summary>
/// O achado A9 pelo cano inteiro, e não só na classe que formata.
///
/// A unidade prova que o tratador monta a resposta certa. Isto prova que ele
/// está LIGADO -- que uma exceção saindo de um controlador atravessa o
/// pipeline, encontra o tratador e sai como <c>application/problem+json</c>.
/// Um handler registrado no lugar errado passa em todo teste de unidade e não
/// atende requisição nenhuma.
/// </summary>
public class ProblemDetailsPipelineTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task AnInternalFailure_AnswersProblemJson_WithoutDriverTextAndWithATraceId()
    {
        using var broken = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRentalRepository>();
                services.AddSingleton<IRentalRepository>(new ThrowingRepository());
            }));
        using var client = broken.CreateDefaultClient(
            new TestGatewayIdentityHandler("Rider", "rider-problem"));
        client.BaseAddress = new Uri("https://localhost");

        var response = await client.GetAsync("/api/Rental/user");

        // 500, e não o 400 que o catch-all devolvia: o cliente mandou uma
        // requisição correta, e dizer 400 pede que ele conserte o que estava
        // certo.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        foreach (var leak in new[]
        {
            "Npgsql", "PostgresException", "relation", "Host=", "ThrowingRepository", "   at "
        })
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }

        using var problem = JsonDocument.Parse(body);
        Assert.Equal("urn:projecty:problem:unexpected", problem.RootElement.GetProperty("type").GetString());
        var traceId = problem.RootElement.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        // 32 hex: é o trace id do W3C, o mesmo que o Tempo indexa. Um
        // identificador que não aparece no trace não acha nada.
        Assert.Equal(32, traceId!.Length);
    }

    /// <summary>
    /// Uma recusa de entrada continua sendo 4xx, e no mesmo formato -- o
    /// cliente não precisa aprender duas formas de erro do mesmo serviço.
    /// </summary>
    [Fact]
    public async Task ARejectedInput_AnswersTheSameShape()
    {
        using var client = factory.CreateAuthenticatedClient("Rider", "rider-problem");

        var response = await client.GetAsync("/api/Rental/batch?ids=not-a-uuid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("urn:projecty:problem:invalid-request", problem.RootElement.GetProperty("type").GetString());
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
        // O identificador recusado não volta: devolvê-lo faria da resposta um
        // refletor da entrada de quem chamou.
        Assert.DoesNotContain("not-a-uuid", body);
    }

    /// <summary>
    /// Um cursor quebrado é erro de quem o mandou, e não falha interna.
    ///
    /// Ele vem da query, e três lugares podem recusá-lo -- o decodificador em
    /// Shared, o Position.Parse dos aluguéis e o das motos. Sem tradução, o
    /// tratador global os leria como 500, dizendo "quebrou aqui dentro" sobre
    /// uma entrada que o cliente pode consertar.
    /// </summary>
    [Theory]
    [InlineData("/api/Rental/user?cursor=not-base64!!")]
    [InlineData("/api/motorcycles?cursor=not-base64!!")]
    public async Task AMalformedCursor_IsAClientError_NotAnInternalOne(string path)
    {
        using var client = factory.CreateAuthenticatedClient("Admin", "an-admin");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "urn:projecty:problem:invalid-request",
            problem.RootElement.GetProperty("type").GetString());
    }

    private sealed class ThrowingRepository : IRentalRepository
    {
        private static Exception Failure() => new InvalidDataException(
            "Npgsql.PostgresException: relation \"rentals\" does not exist (Host=secret-db;Username=root)");

        public Task<Rental> CreateRentalAsync(Rental rental, CancellationToken token = default) => throw Failure();

        public Task<Rental?> GetRentalByIdAsync(string id, CancellationToken token = default) => throw Failure();

        public Task<CursorPage<Rental>> GetRentalsByUserId(
            string userId, string? cursor, int? pageSize, CancellationToken token = default) => throw Failure();

        public Task<IReadOnlyList<Rental>> GetRentalsByIdsAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken token = default) => throw Failure();

        public Task<RentalPreconditions> ReadCreationPreconditionsAsync(
            string riderId, Guid motorcycleId, DateTime startDate, DateTime endDate, CancellationToken token = default) => throw Failure();

        public Task<bool> IsMotorcycleCurrentlyRentedAsync(
            Guid motorcycleId, CancellationToken token = default) => throw Failure();

        public Task UpdateRentalAsync(Rental rental, CancellationToken token = default) => throw Failure();
    }
}
