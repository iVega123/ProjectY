using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using RentalOperations.Repository;
using RentalOperations.Services;
using System.Text;

namespace RentalOperationsTests.Integration;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-key-with-at-least-32-bytes";

    public InMemoryRentalRepository Repository =>
        Services.GetRequiredService<InMemoryRentalRepository>();

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
            services.RemoveAll<IRentalRepository>();
            services.AddSingleton<InMemoryRentalRepository>();
            services.AddSingleton<IRentalRepository>(provider =>
                provider.GetRequiredService<InMemoryRentalRepository>());

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
}
