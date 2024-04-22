using Xunit;
using Moq;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using AuthGate.Controllers;
using AuthGate.Model;
using AuthGate.DTO;
using Microsoft.AspNetCore.Http;
using AuthGateTests.Faker;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AuthGateTests.Unit.Faker;
using Microsoft.Extensions.Configuration;
using AuthGate.Services.RabbitMQ;
using AuthGate.Services.File;

namespace AuthGateTests.Unit.Controllers
{
    public class AuthControllerTests
    {

        private static (
            Mock<FakeUserManager>, 
            Mock<FakeSignInManager<ApplicationUser>>, 
            Mock<FakeRoleManager<IdentityRole>>, 
            Mock<IConfiguration>, 
            Mock<ILogger<AuthController>>,
            Mock<IMessagingPublisherService>,
            Mock<IFileValidationService>
            ) GetMocks()
        {
            var userManagerMock = new Mock<FakeUserManager>();
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var userClaimsPrincipalFactoryMock = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
            var optionsAccessorMock = new Mock<IOptions<IdentityOptions>>();
            var loggerMock = new Mock<ILogger<SignInManager<ApplicationUser>>>();
            var authenticationSchemeProviderMock = new Mock<IAuthenticationSchemeProvider>();
            var userConfirmationMock = new Mock<IUserConfirmation<ApplicationUser>>();

            var signInManagerMock = new Mock<FakeSignInManager<ApplicationUser>>(
                userManagerMock.Object,
                httpContextAccessorMock.Object,
                userClaimsPrincipalFactoryMock.Object,
                optionsAccessorMock.Object,
                loggerMock.Object,
                authenticationSchemeProviderMock.Object,
                userConfirmationMock.Object
            );

            var roleStoreMock = new Mock<IRoleStore<IdentityRole>>();
            var roleValidators = new List<IRoleValidator<IdentityRole>>();
            var keyNormalizerMock = new Mock<ILookupNormalizer>();
            var errorsMock = new Mock<IdentityErrorDescriber>();
            var loggerRoleManagerMock = new Mock<ILogger<RoleManager<IdentityRole>>>();

            var roleManagerMock = new Mock<FakeRoleManager<IdentityRole>>(
                roleStoreMock.Object,
                roleValidators,
                keyNormalizerMock.Object,
                errorsMock.Object,
                loggerRoleManagerMock.Object
            );

            var mockConfig = new Mock<IConfiguration>();

            var mockLogger = new Mock<ILogger<AuthController>>();

            var mockRabbit = new Mock<IMessagingPublisherService>();

            var mockFile = new Mock<IFileValidationService>();

            var testJWtKey = "pnXhunyWll1LgERT86wXwMH5I6ieQC2M";
            mockConfig.Setup(c => c["JwtKey"]).Returns(testJWtKey);

            return (userManagerMock, signInManagerMock, roleManagerMock, mockConfig, mockLogger, mockRabbit, mockFile);
        }

        [Fact]
        public async Task RegisterAdmin_ValidModel_ReturnsOk()
        {
            // Arrange
            var (userManagerMock, signInManagerMock, roleManagerMock, mockConfig, mockLogger, mockRabbit, mockFile) = GetMocks();

            userManagerMock.Setup(mock => mock.CreateAsync(It.IsAny<AdminUser>(), It.IsAny<string>()))
                           .ReturnsAsync(IdentityResult.Success);

            roleManagerMock.Setup(mock => mock.RoleExistsAsync("Admin"))
                   .ReturnsAsync(true);

            roleManagerMock.Setup(mock => mock.CreateAsync(It.IsAny<IdentityRole>()))
                   .ReturnsAsync(IdentityResult.Success);

            userManagerMock.Setup(mock => mock.AddToRoleAsync(It.IsAny<AdminUser>(), "Admin"))
                       .ReturnsAsync(IdentityResult.Success);

            var controller = new AuthController(userManagerMock.Object, signInManagerMock.Object, roleManagerMock.Object, mockConfig.Object, mockLogger.Object, mockFile.Object, mockRabbit.Object);
            var model = new AdminRegisterDto { Email = "test@example.com", Password = "Password@1" };

            // Act
            var result = await controller.RegisterAdmin(model) as OkObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(200, result.StatusCode);
            Assert.NotNull(result.Value);
            userManagerMock.Verify(mock => mock.CreateAsync(It.IsAny<AdminUser>(), It.IsAny<string>()), Times.Once);
            signInManagerMock.Verify(mock => mock.SignInAsync(It.IsAny<AdminUser>(), false, null), Times.Once);
        }

        [Fact]
        public async Task RegisterAdmin_InvalidModel_ReturnsBadRequest()
        {
            // Arrange
            var (userManagerMock, signInManagerMock, roleManagerMock, mockConfig, mockLogger, mockRabbit, mockFile) = GetMocks();
            var controller = new AuthController(userManagerMock.Object, signInManagerMock.Object, roleManagerMock.Object, mockConfig.Object, mockLogger.Object, mockFile.Object, mockRabbit.Object);
            controller.ModelState.AddModelError("Email", "Email is required.");
            var model = new AdminRegisterDto { Email = "", Password = "" };

            // Act
            var result = await controller.RegisterAdmin(model) as BadRequestObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(400, result.StatusCode);
            userManagerMock.Verify(mock => mock.CreateAsync(It.IsAny<AdminUser>(), It.IsAny<string>()), Times.Never);
            signInManagerMock.Verify(mock => mock.SignInAsync(It.IsAny<AdminUser>(), false, null), Times.Never);
        }

        [Fact]
        public async Task RegisterAdmin_UserCreationFailed_ReturnsBadRequestWithErrors()
        {
            // Arrange
            var (userManagerMock, signInManagerMock, roleManagerMock, mockConfig, mockLogger, mockRabbit, mockFile) = GetMocks();

            var controller = new AuthController(userManagerMock.Object, signInManagerMock.Object, roleManagerMock.Object, mockConfig.Object, mockLogger.Object, mockFile.Object, mockRabbit.Object);
            var error = new IdentityError { Code = "DuplicateEmail", Description = "Email is already taken." };
            userManagerMock.Setup(mock => mock.CreateAsync(It.IsAny<AdminUser>(), It.IsAny<string>()))
                   .ReturnsAsync(IdentityResult.Failed(error));
            roleManagerMock.Setup(mock => mock.RoleExistsAsync("Admin"))
                    .ReturnsAsync(true);

            roleManagerMock.Setup(mock => mock.CreateAsync(It.IsAny<IdentityRole>()))
                   .ReturnsAsync(IdentityResult.Success);

            userManagerMock.Setup(mock => mock.AddToRoleAsync(It.IsAny<AdminUser>(), "Admin"))
                       .ReturnsAsync(IdentityResult.Success);
            var model = new AdminRegisterDto { Email = "test@example.com", Password = "password" };

            // Act
            var result = await controller.RegisterAdmin(model) as BadRequestObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(400, result.StatusCode);
            Assert.NotNull(result.Value);
            var errors = (List<IdentityError>)result.Value;
            Assert.Contains(errors, error => error.Code == "DuplicateEmail");
            userManagerMock.Verify(mock => mock.CreateAsync(It.IsAny<AdminUser>(), It.IsAny<string>()), Times.Once);
            signInManagerMock.Verify(mock => mock.SignInAsync(It.IsAny<AdminUser>(), false, null), Times.Never);
        }

        [Fact]
        public async Task Login_ValidCredentials_ReturnsOk()
        {
            var (userManagerMock, signInManagerMock, roleManagerMock, mockConfig, mockLogger, mockRabbit, mockFile) = GetMocks();

            var role = "Admin";

            var adminRoles = new List<string>();

            adminRoles.Add(role);

            var user = new ApplicationUser { Id = "userId", UserName = "test@example.com", Email = "test@example.com" };
            userManagerMock.Setup(mock => mock.FindByEmailAsync(It.IsAny<string>()))
                           .ReturnsAsync(user);

            userManagerMock.Setup(mock => mock.GetRolesAsync(It.IsAny<ApplicationUser>()))
                           .ReturnsAsync(adminRoles);

            signInManagerMock.Setup(mock => mock.PasswordSignInAsync(user, It.IsAny<string>(), true, false))
                             .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Success);

            var controller = new AuthController(userManagerMock.Object, signInManagerMock.Object, roleManagerMock.Object, mockConfig.Object, mockLogger.Object, mockFile.Object, mockRabbit.Object);
            var model = new LoginDto { Email = "test@example.com", Password = "password" };

            // Act
            var result = await controller.Login(model) as OkObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(200, result.StatusCode);
            Assert.NotNull(result.Value);
            userManagerMock.Verify(mock => mock.FindByEmailAsync(It.IsAny<string>()), Times.Once);
            signInManagerMock.Verify(mock => mock.PasswordSignInAsync(user, It.IsAny<string>(), true, false), Times.Once);
        }

        [Fact]
        public async Task Login_InvalidCredentials_ReturnsUnauthorized()
        {
            // Arrange
            var (userManagerMock, signInManagerMock, roleManagerMock, mockConfig, mockLogger, mockRabbit, mockFile) = GetMocks();

            userManagerMock.Setup(mock => mock.FindByEmailAsync(It.IsAny<string>()))
                           .ReturnsAsync((ApplicationUser)null);

            var controller = new AuthController(userManagerMock.Object, signInManagerMock.Object, roleManagerMock.Object, mockConfig.Object, mockLogger.Object, mockFile.Object, mockRabbit.Object);
            var model = new LoginDto { Email = "nonexistent@example.com", Password = "password" };

            // Act
            var result = await controller.Login(model) as UnauthorizedResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(401, result.StatusCode);
            userManagerMock.Verify(mock => mock.FindByEmailAsync(It.IsAny<string>()), Times.Once);
            signInManagerMock.Verify(mock => mock.PasswordSignInAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>(), true, false), Times.Never);
        }

        [Fact]
        public async Task Logout_LogoutSuccessful_ReturnsOk()
        {
            // Arrange
            var (userManagerMock, signInManagerMock, roleManagerMock, mockConfig, mockLogger, mockRabbit, mockFile) = GetMocks();
            var logoutDto = new LogoutDto { Email = "user@example.com" };

            signInManagerMock.Setup(mock => mock.SignOutAsync())
                             .Returns(Task.CompletedTask);

            var controller = new AuthController(userManagerMock.Object, signInManagerMock.Object, roleManagerMock.Object, mockConfig.Object, mockLogger.Object, mockFile.Object, mockRabbit.Object);

            // Act
            var result = await controller.Logout(logoutDto) as OkResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(200, result.StatusCode);
            signInManagerMock.Verify(mock => mock.SignOutAsync(), Times.Once);
        }


        [Fact]
        public async Task RegisterRider_ValidModel_ReturnsOk()
        {
            // Arrange
            var (userManagerMock, signInManagerMock, roleManagerMock, mockConfig, mockLogger, mockRabbit, mockFile) = GetMocks();
            userManagerMock.Setup(mock => mock.CreateAsync(It.IsAny<RiderUser>(), It.IsAny<string>()))
                           .ReturnsAsync(IdentityResult.Success);

            roleManagerMock.Setup(mock => mock.RoleExistsAsync("Rider"))
                   .ReturnsAsync(true);

            roleManagerMock.Setup(mock => mock.CreateAsync(It.IsAny<IdentityRole>()))
                   .ReturnsAsync(IdentityResult.Success);

            userManagerMock.Setup(mock => mock.AddToRoleAsync(It.IsAny<RiderUser>(), "Rider"))
                       .ReturnsAsync(IdentityResult.Success);
            var controller = new AuthController(userManagerMock.Object, signInManagerMock.Object, roleManagerMock.Object, mockConfig.Object, mockLogger.Object, mockFile.Object, mockRabbit.Object);
            //mock file
            var content = "Hello World from a Fake File";
            var fileName = "test.pdf";
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(content);
            writer.Flush();
            stream.Position = 0;
            IFormFile file = new FormFile(stream, 0, stream.Length, "id_from_form", fileName);

            var model = new RiderRegisterDto
            {
                Name = "Teste",
                Email = "test@example.com",
                Password = "password",
                CNPJ = "12345678901234",
                DateOfBirth = new System.DateTime(1990, 1, 1),
                CNHNumber = "1234567890",
                CNHType = "A",
                CNHImage = file
            };

            // Act
            var result = await controller.RegisterRider(model) as OkObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(200, result.StatusCode);
            Assert.NotNull(result.Value);
            userManagerMock.Verify(mock => mock.CreateAsync(It.IsAny<RiderUser>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task RegisterRider_InvalidModel_ReturnsBadRequest()
        {
            // Arrange
            var (userManagerMock, signInManagerMock, roleManagerMock, mockConfig, mockLogger, mockRabbit, mockFile) = GetMocks();
            var controller = new AuthController(userManagerMock.Object, signInManagerMock.Object, roleManagerMock.Object, mockConfig.Object, mockLogger.Object, mockFile.Object, mockRabbit.Object);
            controller.ModelState.AddModelError("Email", "Email is required.");

            //mock file
            var content = "Hello World from a Fake File";
            var fileName = "test.pdf";
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(content);
            writer.Flush();
            stream.Position = 0;
            IFormFile file = new FormFile(stream, 0, stream.Length, "id_from_form", fileName);

            var model = new RiderRegisterDto
            {
                Name = "test",
                Email = "test",
                Password = "password",
                CNPJ = "12345678901234",
                DateOfBirth = new System.DateTime(1990, 1, 1),
                CNHNumber = "1234567890",
                CNHType = "A",
                CNHImage = file
            };

            // Act
            var result = await controller.RegisterRider(model) as BadRequestObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(400, result.StatusCode);
            userManagerMock.Verify(mock => mock.CreateAsync(It.IsAny<RiderUser>(), It.IsAny<string>()), Times.Never);
            signInManagerMock.Verify(mock => mock.SignInAsync(It.IsAny<RiderUser>(), false, null), Times.Never);
        }

        [Fact]
        public async Task RegisterRider_UserCreationFailed_ReturnsBadRequestWithErrors()
        {
            // Arrange
            var error = new IdentityError { Code = "DuplicateEmail", Description = "Email is already taken." };
            var (userManagerMock, signInManagerMock, roleManagerMock, mockConfig, mockLogger, mockRabbit, mockFile) = GetMocks();
            userManagerMock.Setup(mock => mock.CreateAsync(It.IsAny<RiderUser>(), It.IsAny<string>()))
                           .ReturnsAsync(IdentityResult.Failed(error));

            var controller = new AuthController(userManagerMock.Object, signInManagerMock.Object, roleManagerMock.Object, mockConfig.Object, mockLogger.Object, mockFile.Object, mockRabbit.Object);

            //mock file
            var content = "Hello World from a Fake File";
            var fileName = "test.pdf";
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(content);
            writer.Flush();
            stream.Position = 0;
            IFormFile file = new FormFile(stream, 0, stream.Length, "id_from_form", fileName);
            var model = new RiderRegisterDto
            {
                Name = "test",
                Email = "test@example.com",
                Password = "Password@1",
                CNPJ = "92.805.586/0001-80",
                DateOfBirth = new System.DateTime(1990, 1, 1),
                CNHNumber = "33022684637",
                CNHType = "A",
                CNHImage = file
            };

            // Act
            var result = await controller.RegisterRider(model) as BadRequestObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(400, result.StatusCode);
            Assert.NotNull(result.Value);
            var errors = (List<IdentityError>)result.Value;
            Assert.Contains(errors, error => error.Code == "DuplicateEmail");
            userManagerMock.Verify(mock => mock.CreateAsync(It.IsAny<RiderUser>(), It.IsAny<string>()), Times.Once);
            signInManagerMock.Verify(mock => mock.SignInAsync(It.IsAny<RiderUser>(), false, null), Times.Never);
        }
    }
}