using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Moq;
using RentalOperations.Controllers;
using RentalOperations.DTOs;
using RentalOperations.Services;
using System.Net;
using System.Security.Claims;

namespace RentalOperationsTests.Unit;

public sealed class DependencyFailureTests
{
    [Fact]
    public async Task DatabaseFailure_RefusesWith503AndRetryAfter_WithoutLeakingException()
    {
        var controller = ControllerFor(new MongoException("private connection details"));
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

    [Fact]
    public async Task UnavailableMongoServer_FailsWithinThePublicGatewayDeadline()
    {
        var database = new RentalOperations.Data.MongoDbContext("mongodb://127.0.0.1:1", "unavailable");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var error = await Record.ExceptionAsync(() => database.Database
            .GetCollection<MongoDB.Bson.BsonDocument>("probe")
            .Find(FilterDefinition<MongoDB.Bson.BsonDocument>.Empty).AnyAsync());
        Assert.NotNull(error);
        Assert.True(DependencyFailure.IsUnavailable(error));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2.5), $"Driver took {timer.Elapsed}");
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AdminMutation_DependencyFailure_ReturnsSanitized503(bool rename, bool timeout)
    {
        var service = new Mock<IRentalService>();
        Exception failure = timeout
            ? new TimeoutException("private connection details")
            : new MongoException("private connection details");
        service.Setup(s => s.TryRetireMotorcycleAsync("OLD")).ThrowsAsync(failure);
        service.Setup(s => s.TryReserveLicensePlateRenameAsync("OLD", "NEW")).ThrowsAsync(failure);
        var controller = new RentalController(service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = rename
            ? await controller.TryReserveMotorcycleRename(new MotorcycleRenameReservationDto { OldLicencePlate = "OLD", NewLicencePlate = "NEW" })
            : await controller.TryRetireMotorcycle("OLD");
        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("1", controller.Response.Headers.RetryAfter.ToString());
        Assert.DoesNotContain("private", Assert.IsType<ProblemDetails>(response.Value).Detail);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AdminMutation_PreservesSuccessAndConflict(bool rename, bool acquired)
    {
        var service = new Mock<IRentalService>();
        service.Setup(s => s.TryRetireMotorcycleAsync("OLD")).ReturnsAsync(acquired);
        service.Setup(s => s.TryReserveLicensePlateRenameAsync("OLD", "NEW")).ReturnsAsync(acquired);
        var controller = new RentalController(service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = rename
            ? await controller.TryReserveMotorcycleRename(new MotorcycleRenameReservationDto { OldLicencePlate = "OLD", NewLicencePlate = "NEW" })
            : await controller.TryRetireMotorcycle("OLD");
        if (acquired) Assert.IsType<NoContentResult>(result);
        else Assert.IsType<ConflictObjectResult>(result);
        Assert.False(controller.Response.Headers.ContainsKey("Retry-After"));
    }


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
