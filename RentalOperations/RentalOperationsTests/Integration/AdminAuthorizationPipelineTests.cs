using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using RentalOperations.Model;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;

namespace RentalOperationsTests.Integration;

public class AdminAuthorizationPipelineTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AdminAuthorizationPipelineTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminEndpoint_WithRiderToken_ReturnsForbidden()
    {
        using var client = CreateClient("Rider");

        var response = await client.GetAsync("/api/Rental/user/another-user");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_WithAdminToken_ReturnsOk()
    {
        using var client = CreateClient("Admin");

        var response = await client.GetAsync("/api/Rental/user/another-user");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CalculateFinalCost_ForAnotherRidersRental_ReturnsForbiddenAndLeavesRentalUnchanged()
    {
        var original = _factory.Repository.SeedRental(new Rental
        {
            MotorcycleLicencePlate = "OWNER-001",
            UserId = "rider-a",
            StartDate = DateTime.UtcNow.Date,
            PredictedEndDate = DateTime.UtcNow.Date.AddDays(7),
            InitCost = 210m
        });
        var rentalId = original._id!.Value.ToString();
        using var client = CreateClient("Rider", "rider-b");
        var actualEndDate = Uri.EscapeDataString(original.PredictedEndDate.ToString("O"));

        var response = await client.PostAsync(
            $"/api/Rental/calculate-final-cost?rentalId={rentalId}&actualEndDate={actualEndDate}",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var persisted = _factory.Repository.FindRental(rentalId);
        Assert.NotNull(persisted);
        Assert.Equal(original.UserId, persisted.UserId);
        Assert.Equal(original.EndDate, persisted.EndDate);
        Assert.Equal(original.FinalCost, persisted.FinalCost);
        Assert.Equal(original.AdditionalCostsOrSavings, persisted.AdditionalCostsOrSavings);
        Assert.Equal(original.StatusMessage, persisted.StatusMessage);
    }

    [Fact]
    public async Task CalculateFinalCost_ForFinalizedRentalOwnedByAnotherRider_ReturnsForbidden()
    {
        var original = _factory.Repository.SeedRental(new Rental
        {
            MotorcycleLicencePlate = "OWNER-002",
            UserId = "rider-a",
            StartDate = DateTime.UtcNow.Date.AddDays(-7),
            EndDate = DateTime.UtcNow.Date,
            PredictedEndDate = DateTime.UtcNow.Date,
            InitCost = 210m,
            FinalCost = 210m,
            StatusMessage = "Already finalized."
        });
        var rentalId = original._id!.Value.ToString();
        using var client = CreateClient("Rider", "rider-b");
        var actualEndDate = Uri.EscapeDataString(original.EndDate!.Value.ToString("O"));

        var response = await client.PostAsync(
            $"/api/Rental/calculate-final-cost?rentalId={rentalId}&actualEndDate={actualEndDate}",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CalculateFinalCost_ForOwnedRental_PreservesOwner()
    {
        var original = _factory.Repository.SeedRental(new Rental
        {
            MotorcycleLicencePlate = "OWNER-003",
            UserId = "rider-a",
            StartDate = DateTime.UtcNow.Date,
            PredictedEndDate = DateTime.UtcNow.Date.AddDays(7),
            InitCost = 210m
        });
        var rentalId = original._id!.Value.ToString();
        using var client = CreateClient("Rider", original.UserId);
        var actualEndDate = Uri.EscapeDataString(original.PredictedEndDate.ToString("O"));

        var response = await client.PostAsync(
            $"/api/Rental/calculate-final-cost?rentalId={rentalId}&actualEndDate={actualEndDate}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = _factory.Repository.FindRental(rentalId);
        Assert.NotNull(persisted);
        Assert.Equal(original.UserId, persisted.UserId);
        Assert.Equal(original.PredictedEndDate, persisted.EndDate);
        Assert.Equal(original.InitCost, persisted.FinalCost);
    }

    private HttpClient CreateClient(string role, string userId = "requesting-user")
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(role, userId));
        return client;
    }

    private static string CreateToken(string role, string userId)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(CustomWebApplicationFactory.JwtKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "projecty.auth-gate",
            audience: "projecty.rental-operations",
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, role)
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
