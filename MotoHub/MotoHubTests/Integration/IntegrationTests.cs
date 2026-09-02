using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using MotoHub.DTOs;
using MotoHub.Models;
using MotoHub.Services;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace MotoHubTests.Integration
{
    public class IntegrationTests : IClassFixture<CustomWebApplicationFactory<Program>>
    {
        private static int _plateSequence;
        private readonly CustomWebApplicationFactory<Program> _factory;

        public IntegrationTests(CustomWebApplicationFactory<Program> factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Update_CommitsMotorcycleAndOutboxMessageTogether()
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var repositoryContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            Assert.Same(context, repositoryContext);

            var motorcycle = new Motorcycle
            {
                Id = Guid.NewGuid().ToString(),
                LicensePlate = $"OUT-{Guid.NewGuid():N}".ToUpperInvariant(),
                Model = "Transactional outbox",
                Year = 2026,
                RegistrationDate = DateTime.UtcNow
            };
            context.Motorcycles.Add(motorcycle);
            await context.SaveChangesAsync();

            var service = scope.ServiceProvider.GetRequiredService<IMotorcycleService>();
            var newLicencePlate = $"NEW-{Guid.NewGuid():N}".ToUpperInvariant();
            await service.UpdateMotorcycleAsync(motorcycle.LicensePlate, newLicencePlate);

            var message = Assert.Single(context.OutboxMessages.Local);
            Assert.Equal(motorcycle.Id, message.AggregateId);
            Assert.Equal(newLicencePlate, motorcycle.LicensePlate);
            Assert.Equal(1, await context.OutboxMessages.CountAsync(item => item.Id == message.Id));
        }

        [Fact]
        public async Task GetAll_ReturnsSuccessStatusCode()
        {
            // Arrange
            var client = _factory.CreateClient();
            var token = GenerateGatewayIdentityMarker();

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
            var motorcycle = new MotorcycleDTO { LicensePlate = NextPlate(), Model = "Honda", Year = 2020 };

            var token = GenerateGatewayIdentityMarker();

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
        public async Task Create_IgnoresClientSuppliedRetirementMetadata()
        {
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                GenerateGatewayIdentityMarker());
            var licensePlate = NextPlate();
            var response = await client.PostAsJsonAsync("/api/motorcycles", new MotorcycleDTO
            {
                LicensePlate = licensePlate,
                Model = "Must remain active",
                Year = 2026,
                RetiredAtUtc = DateTime.UtcNow,
                RetirementReason = "client-controlled"
            });
            response.EnsureSuccessStatusCode();

            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var motorcycle = await context.Motorcycles.SingleAsync(candidate =>
                candidate.LicensePlate == licensePlate);

            Assert.Null(motorcycle.RetiredAtUtc);
            Assert.Null(motorcycle.RetirementReason);
        }

        [Fact]
        public async Task GetByLicensePlate_ExistingPlate_ReturnsOk()
        {
            // Arrange
            var client = _factory.CreateClient();
            var licensePlate = NextPlate();
            var token = GenerateGatewayIdentityMarker();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var createResponse = await client.PostAsJsonAsync(
                "/api/motorcycles",
                new MotorcycleDTO { LicensePlate = licensePlate, Model = "Honda", Year = 2020 });
            createResponse.EnsureSuccessStatusCode();

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
            var token = GenerateGatewayIdentityMarker();
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
            var originalLicensePlate = NextPlate();
            var newLicensePlate = NextPlate();
            var motorcycle = new MotorcycleDTO { LicensePlate = originalLicensePlate, Model = "Honda", Year = 2020 };
            var updatedMotorcycle = new MotorcycleDTO { LicensePlate = newLicensePlate, Model = "UpdatedModel", Year = 2021 };

            var token = GenerateGatewayIdentityMarker();
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

            var token = GenerateGatewayIdentityMarker();
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
            var licensePlate = NextPlate();

            var token = GenerateGatewayIdentityMarker();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var createResponse = await client.PostAsJsonAsync(
                "/api/motorcycles",
                new MotorcycleDTO { LicensePlate = licensePlate, Model = "Honda", Year = 2020 });
            createResponse.EnsureSuccessStatusCode();

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

            var token = GenerateGatewayIdentityMarker();
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
            var motorcycle = new MotorcycleDTO { LicensePlate = NextPlate(), Model = "Honda", Year = 2020 };

            var token = GenerateGatewayIdentityMarker();
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
            var invalidToken = GenerateInvalidIdentityMarker();

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);

            // Act
            var response = await client.GetAsync("/api/motorcycles");

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Theory]
        [InlineData("ABC123")]
        [InlineData("ABC-1234")]
        [InlineData("prefixABC1234suffix")]
        public async Task Create_InvalidLicensePlate_ReturnsValidationError(string licensePlate)
        {
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                GenerateGatewayIdentityMarker());

            var response = await client.PostAsJsonAsync("/api/motorcycles", new MotorcycleDTO
            {
                LicensePlate = licensePlate,
                Model = "Honda",
                Year = 2020
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("placa", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(1899)]
        [InlineData(3000)]
        public async Task Create_ImplausibleYear_ReturnsValidationError(int year)
        {
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                GenerateGatewayIdentityMarker());

            var response = await client.PostAsJsonAsync("/api/motorcycles", new MotorcycleDTO
            {
                LicensePlate = NextPlate(),
                Model = "Honda",
                Year = year
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("ano", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_ModelThatBecomesTooShortAfterTrimming_ReturnsValidationError()
        {
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                GenerateGatewayIdentityMarker());

            var response = await client.PostAsJsonAsync("/api/motorcycles", new MotorcycleDTO
            {
                LicensePlate = NextPlate(),
                Model = " A",
                Year = 2020
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Model", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GetAll_WithLegacyApiKey_ReturnsUnauthorized()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-API-KEY", "retired-api-key");

            var response = await client.GetAsync("/api/motorcycles");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetAll_WithForgedGatewayIdentity_ReturnsUnauthorized()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("x-identity-key-id", "test-v1");
            client.DefaultRequestHeaders.Add("x-identity-subject", "forged-admin");
            client.DefaultRequestHeaders.Add("x-identity-roles", "Admin");
            client.DefaultRequestHeaders.Add(
                "x-identity-issued-at",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            client.DefaultRequestHeaders.Add("x-identity-signature", "v1=forged");

            var response = await client.GetAsync("/api/motorcycles");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        private static string NextPlate() =>
            $"TST{Interlocked.Increment(ref _plateSequence) % 10000:D4}";

        private string GenerateInvalidIdentityMarker() => "invalid";


        private string GenerateGatewayIdentityMarker() => "valid-admin";
    }
}
