using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Npgsql;
using RentalOperations.Controllers;
using RentalOperations.DTOs;
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

    [Fact]
    public async Task DatabaseFailure_RefusesWith503AndRetryAfter_WithoutLeakingException()
    {
        var controller = ControllerFor(new NpgsqlException("private connection details"));
        var response = Assert.IsType<ObjectResult>(await controller.CreateRental(Request()));
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("1", controller.Response.Headers.RetryAfter.ToString());
        Assert.DoesNotContain("private", Assert.IsType<ProblemDetails>(response.Value).Detail);
    }

    [Fact]
    public async Task WrappedUpstreamFailure_RefusesWith503()
    {
        var controller = ControllerFor(new Exception("wrapper", new HttpRequestException(
            "upstream", null, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.CreateRental(Request())).StatusCode);
    }

    [Fact]
    public async Task BusinessRejection_Remains400_AndDoesNotAdvertiseDependencyRetry()
    {
        var controller = ControllerFor(new ArgumentException("Rider does not exist."));
        Assert.IsType<BadRequestObjectResult>(await controller.CreateRental(Request()));
        Assert.False(controller.Response.Headers.ContainsKey("Retry-After"));
        Assert.False(DependencyFailure.IsUnavailable(new HttpRequestException("missing", null, HttpStatusCode.NotFound)));
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

    private static PostgresException BuildPostgresException(string sqlState) =>
        new("message", "ERROR", "ERROR", sqlState);

    private static RentalCreateDto Request() => new()
    {
        MotocycleLicencePlate = "ABC1D23",
        StartDate = DateTime.UtcNow.AddDays(1),
        PredictedEndDate = DateTime.UtcNow.AddDays(8)
    };

    private static RentalController ControllerFor(Exception failure)
    {
        var service = new Mock<IRentalService>();
        service.Setup(item => item.CreateRentalAsync(It.IsAny<RentalCreateDto>(), "rider")).ThrowsAsync(failure);
        return new RentalController(service.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "rider")], "test"))
                }
            }
        };
    }
}
