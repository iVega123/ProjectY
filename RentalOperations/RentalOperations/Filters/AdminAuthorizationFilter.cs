using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace RentalOperations.Filters
{
    public class AdminAuthorizationFilter : Attribute, IAuthorizationFilter
    {
        private readonly IConfiguration _configuration;

        public AdminAuthorizationFilter(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var userIdentity = context.HttpContext.User.Identity as ClaimsIdentity;
            var hasAdminClaim = userIdentity?.Claims.Any(
                claim => claim.Type == ClaimTypes.Role && claim.Value == "Admin") ?? false;
            if (hasAdminClaim)
            {
                return;
            }

            var expectedApiKey = _configuration["RentalOperationsApiKey"];
            var actualApiKey = context.HttpContext.Request.Headers["X-API-Key"];
            if (!string.IsNullOrWhiteSpace(actualApiKey) && actualApiKey == expectedApiKey)
            {
                return;
            }

            var token = GetBearerToken(context.HttpContext.Request.Headers.Authorization);
            if (token is null || !ValidateTokenAndCheckAdmin(token, out var isAdmin))
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            if (!isAdmin)
            {
                context.Result = new ForbidResult();
            }
        }

        private static string? GetBearerToken(string? authorizationHeader)
        {
            if (string.IsNullOrWhiteSpace(authorizationHeader) ||
                !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var token = authorizationHeader["Bearer ".Length..].Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        private bool ValidateTokenAndCheckAdmin(string token, out bool isAdmin)
        {
            isAdmin = false;
            var tokenHandler = new JwtSecurityTokenHandler();
            var jwtKey = _configuration["JwtKey"] ?? throw new InvalidOperationException("JwtKey is not set in the configuration.");
            var keyBytes = Encoding.UTF8.GetBytes(jwtKey);
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                ValidateIssuer = false,
                ValidateAudience = false
            };

            try
            {
                var principal = tokenHandler.ValidateToken(token, validationParameters, out var securityToken);
                if (securityToken is null || principal is null)
                {
                    return false;
                }

                var userIdentity = principal.Identity as ClaimsIdentity;
                isAdmin = userIdentity?.Claims.Any(
                    claim => claim.Type == ClaimTypes.Role && claim.Value == "Admin") ?? false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
