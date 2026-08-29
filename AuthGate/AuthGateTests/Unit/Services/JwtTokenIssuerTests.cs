using AuthGate.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Xunit;

namespace AuthGateTests.Unit.Services;

public class JwtTokenIssuerTests
{
    private const string Issuer = "projecty.auth-gate";
    private const string MotoHubAudience = "projecty.moto-hub";
    private const string RentalAudience = "projecty.rental-operations";
    private const string MotoHubKey = "test-only-moto-hub-signing-key-0001";
    private const string RentalKey = "test-only-rental-signing-key-000002";

    [Fact]
    public void RentalToken_IsRejectedByMotoHubValidation()
    {
        var issuer = CreateIssuer();
        var token = issuer.CreateToken(
            [new Claim(ClaimTypes.NameIdentifier, "rider-1")],
            "RentalOperations");

        var handler = new JwtSecurityTokenHandler();

        handler.ValidateToken(token, ValidationParameters(RentalKey, RentalAudience), out _);
        Assert.ThrowsAny<SecurityTokenException>(() =>
            handler.ValidateToken(token, ValidationParameters(MotoHubKey, MotoHubAudience), out _));
    }

    private static JwtTokenIssuer CreateIssuer()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audiences:MotoHub"] = MotoHubAudience,
                ["Jwt:Audiences:RentalOperations"] = RentalAudience,
                ["Jwt:SigningKeys:MotoHub"] = MotoHubKey,
                ["Jwt:SigningKeys:RentalOperations"] = RentalKey
            })
            .Build();

        return new JwtTokenIssuer(configuration);
    }

    private static TokenValidationParameters ValidationParameters(string key, string audience) => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = audience
    };
}
