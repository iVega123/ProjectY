using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using MotoHub.Controllers;
using MotoHub.DTOs;
using MotoHub.Entities;
using MotoHub.Services;
using System.Security.Claims;

namespace MotoHubTests.Unit.Controllers
{
    public class MotorcyclesControllerTests
    {
        [Fact]
        public void GetAll_ReturnsOkObjectResult()
        {
            // Arrange
            var motorcycleServiceMock = new Mock<IMotorcycleService>();
            var mockLogger = new Mock<ILogger<MotorcyclesController>>();
            motorcycleServiceMock.Setup(service => service.GetAllMotorcycles()).Returns(new[] { new MotorcycleDTO() { LicensePlate = "test-584" } });
            var controller = new MotorcyclesController(motorcycleServiceMock.Object, mockLogger.Object);

            // Act
            var result = controller.GetAll();

            // Assert
            Assert.IsType<OkObjectResult>(result);
        }

        [Fact]
        public async Task GetByLicensePlate_WithExistingPlate_ReturnsOkObjectResult()
        {
            // Arrange
            var motorcycleServiceMock = new Mock<IMotorcycleService>();
            var mockLogger = new Mock<ILogger<MotorcyclesController>>();
            motorcycleServiceMock.Setup(service => service.GetMotorcycleByLicensePlateAsync("ABC123"))
                                 .ReturnsAsync(new MotorcycleDTO { LicensePlate = "ABC123" });
            var controller = new MotorcyclesController(motorcycleServiceMock.Object, mockLogger.Object);

            // Act
            var result = await controller.GetByLicensePlateAsync("ABC123");

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var motorcycle = Assert.IsType<MotorcycleDTO>(okResult.Value);
            Assert.Equal("ABC123", motorcycle.LicensePlate);
        }

        [Fact]
        public void Create_ValidMotorcycle_ReturnsCreatedAtActionResult()
        {
            // Arrange
            var motorcycleServiceMock = new Mock<IMotorcycleService>();
            var mockLogger = new Mock<ILogger<MotorcyclesController>>();
            motorcycleServiceMock.Setup(service => service.LicensePlateExists("ABC123")).Returns(false);
            var controller = new MotorcyclesController(motorcycleServiceMock.Object, mockLogger.Object);
            var motorcycle = new MotorcycleDTO { LicensePlate = "ABC123", Model = "Honda", Year = 2020 };

            // Act
            var result = controller.Create(motorcycle);

            // Assert
            Assert.IsType<OkObjectResult>(result);
        }

        [Fact]
        public async Task Update_WithExistingLicensePlate_ReturnsNoContentResult()
        {
            // Arrange
            var motorcycleServiceMock = new Mock<IMotorcycleService>();
            var mockLogger = new Mock<ILogger<MotorcyclesController>>();
            motorcycleServiceMock.Setup(service => service.GetMotorcycleByLicensePlateAsync("ABC123"))
                                 .ReturnsAsync(new MotorcycleDTO { LicensePlate = "test-584" });
            motorcycleServiceMock.Setup(service => service.UpdateMotorcycleAsync("ABC123", "XYZ987"))
                                 .Returns(Task.CompletedTask);  // Assuming the update method is void async

            var controller = new MotorcyclesController(motorcycleServiceMock.Object, mockLogger.Object);

            // Act
            var result = await controller.Update("ABC123", "XYZ987");

            // Assert
            Assert.IsType<NoContentResult>(result);
        }

        [Fact]
        public async Task Delete_WithExistingLicensePlate_ReturnsOkResult()
        {
            // Arrange
            var motorcycleServiceMock = new Mock<IMotorcycleService>();
            var mockLogger = new Mock<ILogger<MotorcyclesController>>();
            motorcycleServiceMock.Setup(service => service.DeleteMotorcycle("ABC123"))
                                 .ReturnsAsync(new OperationResult { Success = true, Message = "Deleted Successfully" });
            var controller = new MotorcyclesController(motorcycleServiceMock.Object, mockLogger.Object);

            // Act
            var result = await controller.Delete("ABC123");

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal("Deleted Successfully", okResult.Value);
        }

        [Fact]
        public void Create_DuplicateLicensePlate_ReturnsConflictResult()
        {
            // Arrange
            var motorcycleServiceMock = new Mock<IMotorcycleService>();
            var mockLogger = new Mock<ILogger<MotorcyclesController>>();
            motorcycleServiceMock.Setup(service => service.LicensePlateExists("ABC123")).Returns(true);
            var controller = new MotorcyclesController(motorcycleServiceMock.Object, mockLogger.Object);
            var motorcycle = new MotorcycleDTO { LicensePlate = "ABC123", Model = "Honda", Year = 2020 };

            // Act
            var result = controller.Create(motorcycle);

            // Assert
            Assert.IsType<ConflictObjectResult>(result);
        }

        [Fact]
        public async Task Update_NonExistingLicensePlate_ReturnsNotFoundResult()
        {
            // Arrange
            var motorcycleServiceMock = new Mock<IMotorcycleService>();
            var mockLogger = new Mock<ILogger<MotorcyclesController>>();
            motorcycleServiceMock.Setup(service => service.GetMotorcycleByLicensePlateAsync("XYZ789")).ReturnsAsync((MotorcycleDTO)null);
            var controller = new MotorcyclesController(motorcycleServiceMock.Object, mockLogger.Object);

            // Act
            var result = await controller.Update("XYZ789", "test-584");

            // Assert
            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Delete_NonExistingLicensePlate_ReturnsNotFoundResult()
        {
            // Arrange
            var motorcycleServiceMock = new Mock<IMotorcycleService>();
            var mockLogger = new Mock<ILogger<MotorcyclesController>>();
            motorcycleServiceMock.Setup(service => service.DeleteMotorcycle("XYZ789"))
                                 .ReturnsAsync(new OperationResult { Success = false, Message = "Motorcycle not found" });
            var controller = new MotorcyclesController(motorcycleServiceMock.Object, mockLogger.Object);

            // Act
            var result = await controller.Delete("XYZ789");

            // Assert
            var notFoundResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Motorcycle not found", notFoundResult.Value);
        }

        [Fact]
        public void GetAll_AuthorizedUser_ReturnsOkResult()
        {
            // Arrange
            var controller = new MotorcyclesController(Mock.Of<IMotorcycleService>(), Mock.Of<ILogger<MotorcyclesController>>());
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new Claim[]
                    {
                        new Claim(ClaimTypes.Name, "TestUser"),
                        new Claim(ClaimTypes.Email, "test@example.com"),
                        new Claim(ClaimTypes.Role, "Admin")
                    }, "mock"))
                }
            };

            // Act
            var result = controller.GetAll();

            // Assert
            Assert.IsType<OkObjectResult>(result);
        }

        [Fact]
        public async Task GetByLicensePlate_AuthorizedUser_ReturnsOkResult()
        {
            // Arrange
            var mockService = new Mock<IMotorcycleService>();
            mockService.Setup(service => service.GetMotorcycleByLicensePlateAsync(It.IsAny<string>()))
                       .ReturnsAsync(new MotorcycleDTO { LicensePlate = "ABC123", Model = "Honda", Year = 2020 });
            var mockLogger = new Mock<ILogger<MotorcyclesController>>();
            var controller = new MotorcyclesController(mockService.Object, mockLogger.Object);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new Claim[]
                    {
                new Claim(ClaimTypes.Name, "TestUser"),
                new Claim(ClaimTypes.Email, "test@example.com"),
                new Claim(ClaimTypes.Role, "Admin")
                    }, "mock"))
                }
            };

            // Act
            var result = await controller.GetByLicensePlateAsync("ABC123");

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var model = Assert.IsAssignableFrom<MotorcycleDTO>(okResult.Value);
            Assert.Equal("ABC123", model.LicensePlate);
        }
    }
}
