using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.IdentityModel.Tokens;
using RiderManager.Services.RabbitMQService;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace RiderManager.Filters
{
    public class AdminAuthorizationFilter : Attribute, IAuthorizationFilter
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<MessagingConsumerService> _logger;

        public AdminAuthorizationFilter(IConfiguration configuration, ILogger<MessagingConsumerService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (!IsAuthenticated(context))
            {
                context.Result = new UnauthorizedResult();
            }
        }

        private bool IsAuthenticated(AuthorizationFilterContext context)
        {
            if (IsValidApiKey(context))
            {
                return true;
            }

            var token = GetBearerToken(context.HttpContext.Request.Headers["Authorization"]);
            if (token != null && ValidateTokenAndCheckAdmin(token, out bool isAdmin))
            {
                return isAdmin;
            }

            return false;
        }

        private bool IsValidApiKey(AuthorizationFilterContext context)
        {
            var expectedApiKey = _configuration["RiderManagerApiKey"];
            var actualApiKey = context.HttpContext.Request.Headers["X-API-Key"];

            return !string.IsNullOrWhiteSpace(actualApiKey) && actualApiKey == expectedApiKey;
        }

        private string? GetBearerToken(string authorizationHeader)
        {
            if (string.IsNullOrWhiteSpace(authorizationHeader))
            {
                return null;
            }

            return authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ?
                   authorizationHeader["Bearer ".Length..] : null;
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
                if (securityToken != null && principal != null)
                {
                    var userIdentity = principal.Identity as ClaimsIdentity;
                    isAdmin = userIdentity?.Claims.Any(c => c.Type == ClaimTypes.Role && c.Value == "Admin") ?? false;
                    return true;
                }
            }
            catch (SecurityTokenException ex)
            {
                _logger.LogError(ex.Message);
            }
            return false;
        }
    }
}