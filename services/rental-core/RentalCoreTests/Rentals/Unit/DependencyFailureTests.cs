using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCore.Errors;
using RentalOperations.Domain;
using RentalOperations.Services;
using System.Net;
using System.Security.Claims;

namespace RentalOperationsTests.Unit;

public sealed class DependencyFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrappedDatabaseFailure_PreservesMetricAndTraceAttribution(bool wrapped)
    {
        using var activity = new System.Diagnostics.Activity("dependency-attribution-test").Start();
        string? measuredDependency = null;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "ProjectY.Resilience" && instrument.Name == "dependency.refusals")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (System.Diagnostics.Activity.Current != activity) return;
            foreach (var tag in tags)
                if (tag.Key == "dependency") measuredDependency = tag.Value?.ToString();
        });
        listener.Start();
        Exception failure = new NpgsqlException("database unavailable");
        if (wrapped) failure = new PreWriteDependencyException(new Exception("wrapper", failure));
        DependencyFailure.Record(failure);
        Assert.Equal("database", measuredDependency);
        Assert.Equal("database", activity.GetTagItem("projecty.degradation"));
        Assert.Equal(System.Diagnostics.ActivityStatusCode.Error, activity.Status);
    }

    /// <summary>
    /// Uma violação de restrição não é indisponibilidade.
    ///
    /// É a distinção que decide entre 409 e 503, e portanto entre "não insista"
    /// e "insista daqui a pouco". Confundi-las faria o cliente repetir uma
    /// requisição que o banco vai recusar de novo, indefinidamente. Passou a
    /// importar aqui: no MongoDB toda falha vinha como MongoException, e agora
    /// o conflito e a queda chegam pela mesma hierarquia do Npgsql.
    /// </summary>
    [Fact]
    public void ConstraintViolation_IsNotTreatedAsAnUnavailableDependency()
    {
        Assert.False(DependencyFailure.IsUnavailable(
            BuildPostgresException(PostgresErrorCodes.UniqueViolation)));
        Assert.True(DependencyFailure.IsUnavailable(
            BuildPostgresException(PostgresErrorCodes.CannotConnectNow)));
    }

    [Fact]
    public async Task UnreachableDatabase_FailsWithinThePublicGatewayDeadline()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Timeout=2;Command Timeout=2");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var error = await Record.ExceptionAsync(async () =>
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync();
        });

        Assert.NotNull(error);
        Assert.True(DependencyFailure.IsUnavailable(error!));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2.5), $"Driver took {timer.Elapsed}");
    }

    /// <summary>
    /// A resposta é montada pelo tratador, e é lá que estes testes olham.
    ///
    /// Antes eles chamavam o controlador, porque era o controlador que
    /// formatava o erro. Com o #96 o controlador não formata mais nada: ele
    /// deixa a exceção subir, e quem decide status, forma e o que o cliente vê
    /// é o <see cref="ProblemDetailsExceptionHandler"/>.
    /// </summary>
    [Fact]
    public async Task DatabaseFailure_RefusesWith503AndRetryAfter_WithoutLeakingException()
    {
        var (context, problem) = await Handle(new NpgsqlException("Host=secret-db;Username=root"));

        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
        Assert.DoesNotContain("secret-db", problem.GetProperty("detail").GetString());
        Assert.Equal("urn:projecty:problem:dependency-unavailable", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task WrappedUpstreamFailure_RefusesWith503()
    {
        var (context, _) = await Handle(new Exception("wrapper", new HttpRequestException(
            "upstream", null, HttpStatusCode.ServiceUnavailable)));

        Assert.Equal(503, context.Response.StatusCode);
    }

    /// <summary>
    /// Uma recusa de negócio não anuncia repetição, e continua sendo 4xx.
    ///
    /// Mudou de 400 para 403 no #96: o corpo estava certo, e reenviá-lo
    /// corrigido não existe -- quem muda a habilitação do piloto não é a
    /// requisição. Um 400 aqui mandava o cliente consertar o que ele mandou.
    /// </summary>
    [Fact]
    public async Task BusinessRejection_StaysAClientError_AndDoesNotAdvertiseDependencyRetry()
    {
        var (context, problem) = await Handle(new RiderNotEntitledException());

        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal("urn:projecty:problem:rider-not-entitled", problem.GetProperty("type").GetString());
        Assert.False(DependencyFailure.IsUnavailable(new HttpRequestException("missing", null, HttpStatusCode.NotFound)));
    }

    /// <summary>
    /// O achado A9, no ponto: nada do que a exceção diz chega ao cliente, e o
    /// que chega é um identificador que aparece no trace.
    /// </summary>
    [Fact]
    public async Task InternalFailure_LeaksNoFrameworkOrDriverText_AndCarriesACorrelationId()
    {
        using var activity = new System.Diagnostics.Activity("problem-details-test").Start();
        var leak = new InvalidDataException(
            "Npgsql.PostgresException: relation \"rentals\" does not exist at RentalOperations.Repository");

        var (context, problem) = await Handle(leak);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var body = problem.GetRawText();
        foreach (var forbidden in new[] { "Npgsql", "relation", "RentalOperations.Repository", "InvalidDataException" })
        {
            Assert.DoesNotContain(forbidden, body);
        }
        Assert.Equal(activity.TraceId.ToString(), problem.GetProperty("traceId").GetString());
    }

    /// <summary>
    /// A causa do conflito fica no log, e não na resposta.
    ///
    /// A corrida perdida no índice único chega como PostgresException, e é ela
    /// que tem o SQLSTATE e o nome da constraint -- o que se procura para
    /// entender por que duas criações se cruzaram. Guardá-la numa propriedade
    /// própria tirava tudo isso do log: o ILogger serializa a cadeia de
    /// InnerException, não uma propriedade inventada.
    /// </summary>
    [Fact]
    public async Task AConflict_KeepsItsDatabaseCauseInTheChain_AndOutOfTheResponse()
    {
        var cause = new PostgresException(
            "duplicate key value violates unique constraint \"one_active_rental_per_motorcycle\"",
            "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);
        var conflict = new ActiveRentalConflictException(Guid.NewGuid(), cause);

        Assert.Same(cause, conflict.InnerException);
        Assert.Contains("one_active_rental_per_motorcycle", conflict.ToString());

        var (context, problem) = await Handle(conflict);

        Assert.Equal(409, context.Response.StatusCode);
        Assert.DoesNotContain("one_active_rental_per_motorcycle", problem.GetRawText());
        Assert.DoesNotContain("23505", problem.GetRawText());
    }

    /// <summary>
    /// O atraso da projeção é 4xx, e nunca 5xx.
    ///
    /// Um 5xx diz ao portão que este upstream está doente: ele repete a
    /// requisição -- POST com Idempotency-Key é repetível -- e conta a resposta
    /// contra o disjuntor. Nada está doente; é o registro do próprio chamador
    /// que ainda não chegou, e repetir agora falha igual. Foi 503 durante uma
    /// revisão do #96, e o benchmark de carga acusou.
    /// </summary>
    [Fact]
    public async Task APendingRiderProjection_IsNotDressedUpAsAnUnhealthyUpstream()
    {
        var (context, problem) = await Handle(new RiderProjectionPendingException("rider-1"));

        Assert.Equal(409, context.Response.StatusCode);
        Assert.InRange(context.Response.StatusCode, 400, 499);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
        // E com tipo próprio: "ainda não" tem de ser distinguível de "não pode".
        Assert.Equal(
            "urn:projecty:problem:rider-projection-pending",
            problem.GetProperty("type").GetString());
    }

    private static async Task<(HttpContext Context, System.Text.Json.JsonElement Problem)> Handle(Exception failure)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/Rental/create";
        context.Response.Body = new MemoryStream();
        var handler = new ProblemDetailsExceptionHandler(
            NullLogger<ProblemDetailsExceptionHandler>.Instance);

        Assert.True(await handler.TryHandleAsync(context, failure, CancellationToken.None));

        context.Response.Body.Position = 0;
        using var document = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        return (context, document.RootElement.Clone());
    }

    private static PostgresException BuildPostgresException(string sqlState) =>
        new("message", "ERROR", "ERROR", sqlState);
}