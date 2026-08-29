using AuthGate.DTO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace AuthGateTests.Integration
{
    public class IntegrationTests : IClassFixture<CustomWebApplicationFactory<Program>>
    {
        private readonly CustomWebApplicationFactory<Program> _factory;

        public IntegrationTests(CustomWebApplicationFactory<Program> factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task RegisterAdmin_Unauthenticated_ReturnsNotFound()
        {
            // Arrange
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            var model = new AdminRegisterDto { Email = "attacker@example.com", Password = "A$Slol123ok" };

            // Act
            var response = await client.PostAsJsonAsync("/api/auth/register/admin", model);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task RegisterRider_ValidModel_ReturnsOk()
        {
            //mock file
            var content = "Hello World from a Fake File";
            var fileName = "test.png";
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(content);
            writer.Flush();
            stream.Position = 0;
            IFormFile file = new FormFile(stream, 0, stream.Length, "id_from_form", fileName);

            // Arrange
            var client = _factory.CreateClient();

            // Create MultipartFormDataContent to hold form data
            var formData = new MultipartFormDataContent();
            formData.Add(new StringContent("Caio"), "Name");
            formData.Add(new StringContent("rider@example.com"), "Email");
            formData.Add(new StringContent("A$Slol123ok"), "Password");
            formData.Add(new StringContent("92.805.586/0001-80"), "CNPJ");
            formData.Add(new StringContent(DateTime.Now.AddYears(-30).ToString()), "DateOfBirth");
            formData.Add(new StringContent("33022684637"), "CNHNumber");
            formData.Add(new StringContent("A"), "CNHType");
            formData.Add(new StreamContent(stream), "CNHImage", fileName);

            // Act
            var response = await client.PostAsync("/api/auth/register/rider", formData);

            // Assert
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            Assert.Contains("Rider user successfully registered.", responseContent);
        }

        [Fact]
        public async Task Login_InvalidCredentials_ReturnsUnauthorized()
        {
            // Arrange
            var client = _factory.CreateClient();
            var model = new LoginDto { Email = "user@example.com", Password = "wrongPassword" };

            // Act
            var response = await client.PostAsJsonAsync("/api/auth/login", model);

            // Assert
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Logout_UserLogsOut_ReturnsOk()
        {
            var logoutModel = new LogoutDto { Email = "user@example.com" };
            // Arrange
            var client = _factory.CreateClient();

            // Act
            var response = await client.PostAsJsonAsync("/api/auth/logout", logoutModel);

            // Assert
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task RegisterRider_MissingCNPJ_ReturnsBadRequest()
        {
            // Mock file
            var content = "Hello World from a Fake File";
            var fileName = "test.pdf";
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(content);
            writer.Flush();
            stream.Position = 0;

            // Arrange
            var client = _factory.CreateClient();

            // Create MultipartFormDataContent
            var formData = new MultipartFormDataContent();
            formData.Add(new StringContent("incomplete@example.com"), "Email");
            formData.Add(new StringContent("A$Slol123ok"), "Password");
            formData.Add(new StringContent(DateTime.Now.AddYears(-25).ToString()), "DataNascimento");
            formData.Add(new StringContent("9876543210"), "NumeroCNH");
            formData.Add(new StringContent("B"), "TipoCNH");
            formData.Add(new StreamContent(stream), "ImagemCNH", fileName);
            formData.Add(new StringContent(""), "CNPJ"); // Intentionally left blank to test validation

            // Act
            var response = await client.PostAsync("/api/auth/register/rider", formData);

            // Assert
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Login_NonExistentUser_ReturnsUnauthorized()
        {
            // Arrange
            var client = _factory.CreateClient();
            var model = new LoginDto { Email = "nonexistent@example.com", Password = "A$Slol123ok" };

            // Act
            var response = await client.PostAsJsonAsync("/api/auth/login", model);

            // Assert
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Login_UserAccountLocked_ReturnsUnauthorized()
        {
            // Arrange
            var client = _factory.CreateClient();
            var model = new LoginDto { Email = "lockeduser@example.com", Password = "A$Slol123ok" };

            // Act
            var response = await client.PostAsJsonAsync("/api/auth/login", model);

            // Assert
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Logout_UserNotLoggedIn_ReturnsOk()
        {
            var logoutModel = new LogoutDto { Email = "user@example.com" };
            // Arrange
            var client = _factory.CreateClient();

            // Act
            var response = await client.PostAsJsonAsync("/api/auth/logout", logoutModel);

            // Assert
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Logout_SessionExpired_ReturnsOk()
        {
            var logoutModel = new LogoutDto { Email = "user@example.com" };
            // Arrange
            var client = _factory.CreateClient();

            // Act
            var response = await client.PostAsJsonAsync("/api/auth/logout", logoutModel);

            // Assert
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task LoginAfterLogout_Success()
        {
            // Arrange
            var client = _factory.CreateClient();
            var loginModel = new LoginDto { Email = "user@example.com", Password = "A$Slol123ok" };
            var logoutModel = new LogoutDto { Email = "user@example.com" };

            // Act
            await _factory.CreateAdminAsync(loginModel.Email, loginModel.Password);
            var loginResponse = await client.PostAsJsonAsync("/api/auth/login", loginModel);
            loginResponse.EnsureSuccessStatusCode();
            var logout = await client.PostAsJsonAsync("/api/auth/logout", logoutModel);
            logout.EnsureSuccessStatusCode();

            // Act
            var response = await client.PostAsJsonAsync("/api/auth/login", loginModel);

            // Assert
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            Assert.Contains("token", responseContent);
        }

        [Fact]
        public async Task RegisterRider_AllInvalidFields_ReturnsBadRequest()
        {
            // Mock file
            var content = "Hello World from a Fake File";
            var fileName = "test.pdf";
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(content);
            writer.Flush();
            stream.Position = 0;

            // Arrange
            var client = _factory.CreateClient();
            var formData = new MultipartFormDataContent();
            formData.Add(new StringContent(""), "Email");
            formData.Add(new StringContent(""), "Password");
            formData.Add(new StringContent(""), "CNPJ");
            formData.Add(new StringContent(DateTime.Now.ToString()), "DataNascimento");
            formData.Add(new StringContent(""), "NumeroCNH");
            formData.Add(new StringContent("C"), "TipoCNH");
            formData.Add(new StreamContent(stream), "ImagemCNH", fileName);

            // Act
            var response = await client.PostAsync("/api/auth/register/rider", formData);

            // Assert
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }

    }
}
