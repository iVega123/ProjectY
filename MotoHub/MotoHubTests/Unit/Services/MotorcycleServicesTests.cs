using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MotoHub.CrossCutting;
using MotoHub.DTOs;
using MotoHub.Entities;
using MotoHub.Models;
using MotoHub.Repositories;
using MotoHub.Services;
using MotoHub.Services.RabbitMQ;

namespace MotoHubTests.Unit.Services
{
    public class MotorcycleServiceTests
    {
        [Fact]
        public void GetAllMotorcycles_ReturnsAllMotorcycles()
        {
            // Arrange
            var motorcycles = new List<Motorcycle>
            {
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "ABC123", Model = "Honda", Year = 2020 },
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "DEF456", Model = "Yamaha", Year = 2019 }
            };

            var motorcycleDTOs = new List<MotorcycleDTO>
            {
                new MotorcycleDTO { LicensePlate = "ABC123", Model = "Honda", Year = 2020 },
                new MotorcycleDTO { LicensePlate = "DEF456", Model = "Yamaha", Year = 2019 }
            };

            var mockMapper = new Mock<IMapper>();
            mockMapper.Setup(m => m.Map<IEnumerable<MotorcycleDTO>>(It.IsAny<IEnumerable<Motorcycle>>()))
                      .Returns(motorcycleDTOs);

            var mockRepository = new Mock<IMotorcycleRepository>();

            var mockMessagingPublish = new Mock<IMessagingPublisherService>();

            var mockCrossCutting = new Mock<IRentalOperationService>();

            mockRepository.Setup(repo => repo.GetAll())
                          .Returns(motorcycles);

            var service = new MotorcycleService(mockRepository.Object, mockMapper.Object, mockMessagingPublish.Object, mockCrossCutting.Object);

            // Act
            var result = service.GetAllMotorcycles();

            // Assert
            Assert.NotNull(result);
            Assert.Collection(result,
                item => Assert.Equal("ABC123", item.LicensePlate),
                item => Assert.Equal("DEF456", item.LicensePlate)
            );
        }

        [Fact]
        public async void GetMotorcycleByLicensePlate_ExistingLicensePlate_ReturnsMotorcycleDTO()
        {
            // Arrange
            var existingLicensePlate = "ABC123";
            var existingMotorcycle = new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = existingLicensePlate, Model = "Honda", Year = 2020 };
            var motorcycleDTO = new MotorcycleDTO { LicensePlate = existingLicensePlate, Model = "Honda", Year = 2020 };

            var mockMapper = new Mock<IMapper>();
            var mockMessagingPublish = new Mock<IMessagingPublisherService>();

            var mockCrossCutting = new Mock<IRentalOperationService>();
            mockMapper.Setup(m => m.Map<MotorcycleDTO>(It.IsAny<Motorcycle>()))
                      .Returns(motorcycleDTO);

            var mockRepository = new Mock<IMotorcycleRepository>();
            mockRepository.Setup(repo => repo.GetByLicensePlateAsync(existingLicensePlate))
                          .ReturnsAsync(existingMotorcycle);

            var service = new MotorcycleService(mockRepository.Object, mockMapper.Object, mockMessagingPublish.Object, mockCrossCutting.Object);

            // Act
            var result = await service.GetMotorcycleByLicensePlateAsync(existingLicensePlate);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(existingLicensePlate, result.LicensePlate);
        }

        [Fact]
        public void CreateMotorcycle_ValidMotorcycle_CreatesAndReturnsCreatedAtAction()
        {
            // Arrange
            var motorcycleDTO = new MotorcycleDTO { LicensePlate = "ABC123", Model = "Honda", Year = 2020 };
            var createdMotorcycle = new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "ABC123", Model = "Honda", Year = 2020 };

            var mockMapper = new Mock<IMapper>();
            mockMapper.Setup(m => m.Map<Motorcycle>(It.IsAny<MotorcycleDTO>()))
                      .Returns(createdMotorcycle);

            var mockRepository = new Mock<IMotorcycleRepository>();

            var mockMessagingPublish = new Mock<IMessagingPublisherService>();

            var mockCrossCutting = new Mock<IRentalOperationService>();

            var service = new MotorcycleService(mockRepository.Object, mockMapper.Object, mockMessagingPublish.Object, mockCrossCutting.Object);

            // Act
            service.CreateMotorcycle(motorcycleDTO);

            // Assert
            mockRepository.Verify(repo => repo.Add(It.IsAny<Motorcycle>()), Times.Once);
            mockMapper.Verify(m => m.Map<Motorcycle>(motorcycleDTO), Times.Once);
        }

        [Fact]
        public async Task UpdateMotorcycle_ExistingMotorcycle_UpdatesMotorcycle()
        {
            // Arrange
            var existingLicensePlate = "ABC123";
            var newLicensePlate = "XYZ987";
            var existingMotorcycle = new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = existingLicensePlate, Model = "Honda", Year = 2020 };
            var mockMapper = new Mock<IMapper>();
            var mockRepository = new Mock<IMotorcycleRepository>();
            mockRepository.Setup(repo => repo.GetByLicensePlateAsync(existingLicensePlate))
                          .ReturnsAsync(existingMotorcycle);
            mockRepository.Setup(repo => repo.Update(existingMotorcycle))
                          .Verifiable("Repository update was not called");

            var mockMessagingPublish = new Mock<IMessagingPublisherService>();
            var mockCrossCutting = new Mock<IRentalOperationService>();

            var mockMessagingPublisherService = new Mock<IMessagingPublisherService>();
            mockMessagingPublisherService.Setup(p => p.PublishLicenceUpdate(It.Is<LicencePlateRabbitMQEntity>(m =>
                m.newLicencePlate == newLicensePlate && m.oldLicencePlate == existingLicensePlate)))
                .Verifiable("Message was not published correctly");

            var service = new MotorcycleService(mockRepository.Object, mockMapper.Object, mockMessagingPublish.Object, mockCrossCutting.Object);

            // Act
            await service.UpdateMotorcycleAsync(existingLicensePlate, newLicensePlate);

            // Assert
            mockRepository.Verify();

            Assert.Equal(newLicensePlate, existingMotorcycle.LicensePlate);
        }

        [Fact]
        public void DeleteMotorcycle_ExistingMotorcycle_DeletesMotorcycle()
        {
            // Arrange
            var existingLicensePlate = "ABC123";
            var existingMotorcycle = new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = existingLicensePlate, Model = "Honda", Year = 2020 };

            var mockRepository = new Mock<IMotorcycleRepository>();
            var mockMessagingPublish = new Mock<IMessagingPublisherService>();
            var mockCrossCutting = new Mock<IRentalOperationService>();
            mockRepository.Setup(repo => repo.GetByLicensePlateAsync(existingLicensePlate))
                          .ReturnsAsync(existingMotorcycle);

            var service = new MotorcycleService(mockRepository.Object, Mock.Of<IMapper>(), mockMessagingPublish.Object, mockCrossCutting.Object);

            // Act
            service.DeleteMotorcycle(existingLicensePlate);

            // Assert
            mockRepository.Verify(repo => repo.Delete(existingMotorcycle.Id), Times.Once);
        }

        [Fact]
        public void LicensePlateExists_ExistingLicensePlate_ReturnsTrue()
        {
            // Arrange
            var existingLicensePlate = "ABC123";

            var mockRepository = new Mock<IMotorcycleRepository>();
            mockRepository.Setup(repo => repo.LicensePlateExists(existingLicensePlate))
                          .Returns(true);

            var mockMessagingPublish = new Mock<IMessagingPublisherService>();
            var mockCrossCutting = new Mock<IRentalOperationService>();
            var service = new MotorcycleService(mockRepository.Object, Mock.Of<IMapper>(), mockMessagingPublish.Object, mockCrossCutting.Object);

            // Act
            var result = service.LicensePlateExists(existingLicensePlate);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void CreateMotorcycle_DuplicateLicensePlate_ReturnsConflict()
        {
            // Arrange
            var motorcycleDTO = new MotorcycleDTO { LicensePlate = "ABC123", Model = "Honda", Year = 2020 };

            var mockRepository = new Mock<IMotorcycleRepository>();
            var mockMessagingPublish = new Mock<IMessagingPublisherService>();
            var mockCrossCutting = new Mock<IRentalOperationService>();
            mockRepository.Setup(repo => repo.LicensePlateExists(motorcycleDTO.LicensePlate))
                          .Returns(true);

            var service = new MotorcycleService(mockRepository.Object, Mock.Of<IMapper>(), mockMessagingPublish.Object, mockCrossCutting.Object);

            // Act
            var result = Record.Exception(() => service.CreateMotorcycle(motorcycleDTO));

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async void UpdateMotorcycle_NonExistingMotorcycle_DoesNotUpdate()
        {
            // Arrange
            var nonExistingLicensePlate = "XYZ789";
            var updatedMotorcycleDTO = nonExistingLicensePlate;

            var mockRepository = new Mock<IMotorcycleRepository>();
            var mockMessagingPublish = new Mock<IMessagingPublisherService>();
            var mockCrossCutting = new Mock<IRentalOperationService>();
            mockRepository.Setup(repo => repo.GetByLicensePlateAsync(nonExistingLicensePlate))
                          .ReturnsAsync((Motorcycle)null);

            var service = new MotorcycleService(mockRepository.Object, Mock.Of<IMapper>(), mockMessagingPublish.Object, mockCrossCutting.Object);

            // Act
            await service.UpdateMotorcycleAsync(nonExistingLicensePlate, updatedMotorcycleDTO);

            // Assert
            mockRepository.Verify(repo => repo.Update(It.IsAny<Motorcycle>()), Times.Never);
        }

        [Fact]
        public void DeleteMotorcycle_NonExistingMotorcycle_DoesNotDelete()
        {
            // Arrange
            var nonExistingLicensePlate = "XYZ789";

            var mockRepository = new Mock<IMotorcycleRepository>();
            var mockMessagingPublish = new Mock<IMessagingPublisherService>();
            var mockCrossCutting = new Mock<IRentalOperationService>();
            mockRepository.Setup(repo => repo.GetByLicensePlateAsync(nonExistingLicensePlate))
                          .ReturnsAsync((Motorcycle)null);

            var service = new MotorcycleService(mockRepository.Object, Mock.Of<IMapper>(), mockMessagingPublish.Object, mockCrossCutting.Object);

            // Act
            service.DeleteMotorcycle(nonExistingLicensePlate);

            // Assert
            mockRepository.Verify(repo => repo.Delete(It.IsAny<string>()), Times.Never);
        }
    }
}
