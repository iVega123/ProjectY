using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace RentalOperations.Filters
{
    public class AuthorizationFilter : Attribute, IAuthorizationFilter
    {
        private readonly IConfiguration _configuration;

        public AuthorizationFilter(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            bool isAuthenticated = false;

            var userIdentity = context.HttpContext.User.Identity as ClaimsIdentity;

            var hasRole = userIdentity?.Claims.Any(c => c.Type == ClaimTypes.Role && (c.Value == "Admin" || c.Value == "Rider"));

            if (hasRole.GetValueOrDefault())
            {
                isAuthenticated = true;
            }

            var expectedApiKey = _configuration["RentalOperationsApiKey"];
            var actualApiKey = context.HttpContext.Request.Headers["X-API-Key"];
            if (!string.IsNullOrWhiteSpace(actualApiKey) && actualApiKey == expectedApiKey)
            {
                isAuthenticated = true;
            }

            if (!isAuthenticated)
            {
                var token = context.HttpContext.Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
                isAuthenticated = ValidateToken(token);
            }

            if (!isAuthenticated)
            {
                context.Result = new UnauthorizedResult();
                return;
            }
        }

        private bool ValidateToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                return false;

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
                var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
                return validatedToken != null && principal != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
