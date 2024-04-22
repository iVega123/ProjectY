using Xunit;
using Moq;
using Microsoft.AspNetCore.Http;
using AuthGate.Services.File;

namespace AuthGateTests.Unit.Controllers
{
    public class FileValidationServiceTests
    {
        [Fact]
        public async Task ValidateAndConvertFileAsync_NullFile_ThrowsArgumentNullException()
        {
            // Arrange
            var service = new FileValidationService();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() => service.ValidateAndConvertFileAsync(null));
        }

        [Fact]
        public async Task ValidateAndConvertFileAsync_UnsupportedExtension_ThrowsInvalidOperationException()
        {
            // Arrange
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.FileName).Returns("testfile.jpg");
            var service = new FileValidationService();

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateAndConvertFileAsync(fileMock.Object));
        }

        [Fact]
        public async Task ValidateAndConvertFileAsync_FileSizeExceedsLimit_ThrowsInvalidOperationException()
        {
            // Arrange
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.FileName).Returns("image.png");
            fileMock.Setup(f => f.Length).Returns(10 * 1024 * 1024);  // 10 MB
            var service = new FileValidationService();

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateAndConvertFileAsync(fileMock.Object));
        }

        [Fact]
        public async Task ValidateAndConvertFileAsync_ValidFile_ReturnsFileStreamAndExtension()
        {
            // Arrange
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.FileName).Returns("image.png");
            fileMock.Setup(f => f.Length).Returns(1024 * 1024); // 1 MB
            var service = new FileValidationService();

            using (var ms = new MemoryStream())
            {
                fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<System.Threading.CancellationToken>()))
                        .Callback<Stream, System.Threading.CancellationToken>((stream, token) => ms.CopyTo(stream));

                // Act
                var result = await service.ValidateAndConvertFileAsync(fileMock.Object);

                // Assert
                Assert.NotNull(result.Item1);
                Assert.Equal(".png", result.Item2);
            }
        }

    }
}