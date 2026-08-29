using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace AuthGate.Services;

public sealed class JwtTokenIssuer
{
    private readonly IConfiguration _configuration;

    public JwtTokenIssuer(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string CreateToken(IEnumerable<Claim> claims, string audienceName)
    {
        var issuer = Require("Jwt:Issuer");
        var audience = _configuration[$"Jwt:Audiences:{audienceName}"];
        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new ArgumentOutOfRangeException(
                nameof(audienceName),
                audienceName,
                "The requested token audience is not supported.");
        }

        var signingKey = Require($"Jwt:SigningKeys:{audienceName}");
        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKeys:{audienceName} must contain at least 32 bytes.");
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string Require(string key) =>
        _configuration[key] ?? throw new InvalidOperationException($"{key} is not configured.");
}
