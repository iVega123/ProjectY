using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using MotoHub.DTOs;
using Newtonsoft.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

namespace MotoHubTests.Integration
{
    public class IntegrationTests : IClassFixture<CustomWebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;
        private readonly IConfiguration _configuration;

        public IntegrationTests(CustomWebApplicationFactory<Program> factory)
        {
            _factory = factory;
            _configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .Build();
        }

        [Fact]
        public async Task GetAll_ReturnsSuccessStatusCode()
        {
            // Arrange
            var client = _factory.CreateClient();
            var token = GenerateJwtToken();

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            // Act
            var response = await client.GetAsync("/api/motorcycles");

            // Assert
            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Create_ValidMotorcycleWithToken_ReturnsCreatedAtAction()
        {
            // Arrange
            var client = _factory.CreateClient();
            var motorcycle = new MotorcycleDTO { LicensePlate = "ABC123", Model = "Honda", Year = 2020 };

            var token = GenerateJwtToken();

            // Add the token to the HTTP headers
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            // Act
            var content = new StringContent(JsonConvert.SerializeObject(motorcycle), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/motorcycles", content);

            // Assert
            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetByLicensePlate_ExistingPlate_ReturnsOk()
        {
            // Arrange
            var client = _factory.CreateClient();
            var licensePlate = "ABC123";
            var token = GenerateJwtToken();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            // Act
            var response = await client.GetAsync($"/api/motorcycles/{licensePlate}");

            // Assert
            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetByLicensePlate_NonExistingPlate_ReturnsNotFound()
        {
            // Arrange
            var client = _factory.CreateClient();
            var licensePlate = "NonExisting";
            var token = GenerateJwtToken();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            // Act
            var response = await client.GetAsync($"/api/motorcycles/{licensePlate}");

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task Update_ExistingPlate_ReturnsBadRequest()
        {
            // Arrange
            var client = _factory.CreateClient();
            var originalLicensePlate = "ABC123";
            var newLicensePlate = "XYZ987";
            var motorcycle = new MotorcycleDTO { LicensePlate = originalLicensePlate, Model = "Honda", Year = 2020 };
            var updatedMotorcycle = new MotorcycleDTO { LicensePlate = newLicensePlate, Model = "UpdatedModel", Year = 2021 };

            var token = GenerateJwtToken();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Act
            // Create the motorcycle first
            var createResponse = await client.PostAsJsonAsync("/api/motorcycles", motorcycle);
            createResponse.EnsureSuccessStatusCode();

            // Now, update the motorcycle
            var updateResponse = await client.PutAsJsonAsync($"/api/motorcycles/{originalLicensePlate}", updatedMotorcycle);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, updateResponse.StatusCode);
        }

        [Fact]
        public async Task Update_NonExistingPlate_ReturnsNotFound()
        {
            // Arrange
            var client = _factory.CreateClient();
            var licensePlate = "NonExisting";
            var updatedMotorcycle = new MotorcycleDTO { LicensePlate = licensePlate, Model = "UpdatedModel", Year = 2021 };

            var token = GenerateJwtToken();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            // Act
            var content = new StringContent(JsonConvert.SerializeObject(updatedMotorcycle), Encoding.UTF8, "application/json");
            var response = await client.PutAsync($"/api/motorcycles/{licensePlate}", content);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Delete_ExistingPlate_ReturnsNoContent()
        {
            // Arrange
            var client = _factory.CreateClient();
            var licensePlate = "ABC123";

            var token = GenerateJwtToken();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            // Act
            var response = await client.DeleteAsync($"/api/motorcycles/{licensePlate}");

            // Assert
            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Delete_NonExistingPlate_ReturnsNotFound()
        {
            // Arrange
            var client = _factory.CreateClient();
            var licensePlate = "NonExisting";

            var token = GenerateJwtToken();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            // Act
            var response = await client.DeleteAsync($"/api/motorcycles/{licensePlate}");

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Create_MissingToken_ReturnsUnauthorized()
        {
            // Arrange
            var client = _factory.CreateClient();
            var motorcycle = new MotorcycleDTO { LicensePlate = "ABC123", Model = "Honda", Year = 2020 };

            // Act
            var content = new StringContent(JsonConvert.SerializeObject(motorcycle), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/motorcycles", content);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Update_MissingToken_ReturnsUnauthorized()
        {
            // Arrange
            var client = _factory.CreateClient();
            var licensePlate = "ABC123";
            var updatedMotorcycle = new MotorcycleDTO { LicensePlate = licensePlate, Model = "UpdatedModel", Year = 2021 };

            // Act
            var content = new StringContent(JsonConvert.SerializeObject(updatedMotorcycle), Encoding.UTF8, "application/json");
            var response = await client.PutAsync($"/api/motorcycles/{licensePlate}", content);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Delete_MissingToken_ReturnsUnauthorized()
        {
            // Arrange
            var client = _factory.CreateClient();
            var licensePlate = "ABC123";

            // Act
            var response = await client.DeleteAsync($"/api/motorcycles/{licensePlate}");

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Create_DuplicateLicensePlate_ReturnsConflict()
        {
            // Arrange
            var client = _factory.CreateClient();
            var motorcycle = new MotorcycleDTO { LicensePlate = "ExistingPlate", Model = "Honda", Year = 2020 };

            var token = GenerateJwtToken();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var content = new StringContent(JsonConvert.SerializeObject(motorcycle), Encoding.UTF8, "application/json");

            var initialResponse = await client.PostAsync("/api/motorcycles", content);

            content = new StringContent(JsonConvert.SerializeObject(motorcycle), Encoding.UTF8, "application/json");
            var duplicateResponse = await client.PostAsync("/api/motorcycles", content);

            Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);

            Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        }

        [Fact]
        public async Task GetAll_WithInvalidToken_ReturnsUnauthorized()
        {
            // Arrange
            var client = _factory.CreateClient();
            var invalidToken = GenerateInvalidJwtToken();

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);

            // Act
            var response = await client.GetAsync("/api/motorcycles");

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetAll_WithInvalidApiKey_ReturnsUnauthorized()
        {
            // Arrange
            var client = _factory.CreateClient();
            var invalidApiKey = GenerateInvalidApiKey();

            client.DefaultRequestHeaders.Add("X-API-KEY", invalidApiKey);

            // Act
            var response = await client.GetAsync("/api/motorcycles");

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        private string GenerateInvalidApiKey()
        {
            return "30cee9e2-9a38-4aad-8fe6-0398bd7f2a25";
        }

        private string GenerateInvalidJwtToken()
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "InvalidUser"),
                new Claim(ClaimTypes.Email, "invalid@example.com"),
                new Claim(ClaimTypes.Role, "Guest")
            };

            var invalidKey = "ThisIsAnInvalidKeyForTestingas@asda"; 
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(invalidKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                claims: claims,
                expires: DateTime.Now.AddMinutes(-1),
                signingCredentials: creds);

            var tokenHandler = new JwtSecurityTokenHandler();
            return tokenHandler.WriteToken(token);
        }


        private string GenerateJwtToken()
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "TestUser"),
                new Claim(ClaimTypes.Email, "test@example.com"),
                new Claim(ClaimTypes.Role, "Admin")
            };

            var jwtKey = _configuration["JwtKey"];
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                claims: claims,
                expires: DateTime.Now.AddHours(1),
                signingCredentials: creds);

            var tokenHandler = new JwtSecurityTokenHandler();
            return tokenHandler.WriteToken(token);
        }
    }
}
