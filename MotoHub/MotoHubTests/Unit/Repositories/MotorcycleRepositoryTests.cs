using Microsoft.EntityFrameworkCore;
using Moq;
using Moq.EntityFrameworkCore;
using MotoHub.Data;
using MotoHub.Models;
using MotoHub.Repositories;

namespace MotoHubTests.Unit.Repositories
{
    public class MotorcycleRepositoryTests
    {
        [Fact]
        public void GetAll_ReturnsAllMotorcycles()
        {
            // Arrange
            var motorcycles = new List<Motorcycle>
            {
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "ABC123", Model = "Honda", Year = 2020 },
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "DEF456", Model = "Yamaha", Year = 2021 }
            };

            var mockContext = new Mock<IApplicationDbContext>();
            mockContext.Setup(c => c.Motorcycles).ReturnsDbSet(motorcycles);

            var repository = new MotorcycleRepository(mockContext.Object);

            // Act
            var result = repository.GetAll();

            // Assert
            Assert.NotNull(result);
            var returnedMotorcycles = result.ToList();
            Assert.Equal(2, returnedMotorcycles.Count());
            Assert.Collection(returnedMotorcycles,
                item => Assert.Equal("ABC123", item.LicensePlate),
                item => Assert.Equal("DEF456", item.LicensePlate)
            );
        }

        [Fact]
        public void GetById_ReturnsCorrectMotorcycle()
        {
            var id = Guid.NewGuid().ToString();
            // Arrange
            var data = new List<Motorcycle>
        {
            new Motorcycle { Id = id, LicensePlate = "ABC123", Model = "Honda", Year = 2020 },
            new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "DEF456", Model = "Yamaha", Year = 2021 }
        }.AsQueryable();

            var mockSet = new Mock<DbSet<Motorcycle>>();
            mockSet.As<IQueryable<Motorcycle>>().Setup(m => m.Provider).Returns(data.Provider);
            mockSet.As<IQueryable<Motorcycle>>().Setup(m => m.Expression).Returns(data.Expression);
            mockSet.As<IQueryable<Motorcycle>>().Setup(m => m.ElementType).Returns(data.ElementType);
            mockSet.As<IQueryable<Motorcycle>>().Setup(m => m.GetEnumerator()).Returns(data.GetEnumerator());
            mockSet.Setup(m => m.Find(It.IsAny<object[]>())).Returns<object[]>(ids => data.FirstOrDefault(d => d.Id == (string)ids[0]));

            var mockContext = new Mock<IApplicationDbContext>();
            mockContext.Setup(c => c.Motorcycles).Returns(mockSet.Object);

            var repository = new MotorcycleRepository(mockContext.Object);

            // Act
            var result = repository.GetById(id);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("ABC123", result.LicensePlate);
            Assert.Equal("Honda", result.Model);
            Assert.Equal(2020, result.Year);
        }

        [Fact]
        public void Add_AddsNewMotorcycle()
        {
            // Arrange
            var motorcycleToAdd = new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "GHI789", Model = "Suzuki", Year = 2022 };

            var mockContext = new Mock<IApplicationDbContext>();
            var mockDbSet = new Mock<DbSet<Motorcycle>>();
            mockContext.Setup(c => c.Motorcycles).Returns(mockDbSet.Object);

            var repository = new MotorcycleRepository(mockContext.Object);

            // Act
            repository.Add(motorcycleToAdd);

            // Assert
            mockDbSet.Verify(dbSet => dbSet.Add(motorcycleToAdd), Times.Once);
            mockContext.Verify(c => c.SaveChanges(), Times.Once);
        }


        [Fact]
        public void LicensePlateExists_ReturnsTrue_WhenLicensePlateExists()
        {
            // Arrange
            var motorcycles = new List<Motorcycle>
            {
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "ABC123", Model = "Honda", Year = 2020 },
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "DEF456", Model = "Yamaha", Year = 2021 }
            };

            var mockContext = new Mock<IApplicationDbContext>();
            mockContext.Setup(c => c.Motorcycles).ReturnsDbSet(motorcycles);

            var repository = new MotorcycleRepository(mockContext.Object);

            // Act
            var result = repository.LicensePlateExists("ABC123");

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void LicensePlateExists_ReturnsFalse_WhenLicensePlateDoesNotExist()
        {
            // Arrange
            var motorcycles = new List<Motorcycle>
            {
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "ABC123", Model = "Honda", Year = 2020 },
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "DEF456", Model = "Yamaha", Year = 2021 }
            };

            var mockContext = new Mock<IApplicationDbContext>();
            mockContext.Setup(c => c.Motorcycles).ReturnsDbSet(motorcycles);

            var repository = new MotorcycleRepository(mockContext.Object);

            // Act
            var result = repository.LicensePlateExists("GHI789");

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async void GetByLicensePlate_ReturnsCorrectMotorcycle_WhenLicensePlateExists()
        {
            // Arrange
            var motorcycles = new List<Motorcycle>
            {
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "ABC123", Model = "Honda", Year = 2020 },
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "DEF456", Model = "Yamaha", Year = 2021 }
            };

            var mockContext = new Mock<IApplicationDbContext>();
            mockContext.Setup(c => c.Motorcycles).ReturnsDbSet(motorcycles);

            var repository = new MotorcycleRepository(mockContext.Object);

            // Act
            var result = await repository.GetByLicensePlateAsync("ABC123");

            // Assert
            Assert.NotNull(result);
            Assert.Equal("ABC123", result.LicensePlate);
        }

        [Fact]
        public void GetById_ReturnsNull_WhenIdDoesNotExist()
        {
            // Arrange
            var motorcycles = new List<Motorcycle>
            {
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "ABC123", Model = "Honda", Year = 2020 },
                new Motorcycle { Id = Guid.NewGuid().ToString(), LicensePlate = "DEF456", Model = "Yamaha", Year = 2021 }
            };

            var mockContext = new Mock<IApplicationDbContext>();
            mockContext.Setup(c => c.Motorcycles.Find(3)).Returns(() => null);

            var repository = new MotorcycleRepository(mockContext.Object);

            // Act
            var result = repository.GetById(Guid.NewGuid().ToString());

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void Add_ThrowsException_WhenMotorcycleIsNull()
        {
            // Arrange
            var mockContext = new Mock<IApplicationDbContext>();
            var repository = new MotorcycleRepository(mockContext.Object);

            // Act & Assert
            Assert.Throws<NullReferenceException>(() => repository.Add(null));
        }
    }
}
