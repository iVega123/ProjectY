using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using RentalOperations.DTOs;
using RentalOperations.Services;
using System.Text;

namespace RentalOperationsTests.Integration;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-key-with-at-least-32-bytes";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = JwtKey,
                ["Jwt:Issuer"] = "projecty.auth-gate",
                ["Jwt:Audience"] = "projecty.rental-operations"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IRentalService>();
            services.AddScoped<IRentalService, StubRentalService>();

            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)),
                        ValidateIssuer = true,
                        ValidIssuer = "projecty.auth-gate",
                        ValidateAudience = true,
                        ValidAudience = "projecty.rental-operations"
                    };
                });
        });
    }

    private sealed class StubRentalService : IRentalService
    {
        public Task CreateRentalAsync(RentalCreateDto createDto, string userId) => Task.CompletedTask;

        public Task<ResponseRentalDTO> CalculateFinalCostAsync(
            string rentalId,
            string userId,
            DateTime actualEndDate) => Task.FromResult(new ResponseRentalDTO());

        public Task<List<ResponseRentalDTO>> GetRentalsByUserIdAsync(string userId) =>
            Task.FromResult(new List<ResponseRentalDTO>
            {
                new() { RentalId = "rental-1", UserId = userId }
            });

        public Task UpdateMotorcycleLicensePlateAsync(string oldLicensePlate, string newLicensePlate) =>
            Task.CompletedTask;

        public Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate) => Task.FromResult(false);
    }
}
