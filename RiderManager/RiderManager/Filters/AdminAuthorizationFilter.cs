using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RiderManager.Services.RabbitMQService;

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
                context.Result = context.HttpContext.User.Identity?.IsAuthenticated == true
                    ? new ForbidResult()
                    : new UnauthorizedResult();
            }
        }

        private bool IsAuthenticated(AuthorizationFilterContext context)
        {
            if (IsValidApiKey(context))
            {
                return true;
            }

            return context.HttpContext.User.Identity?.IsAuthenticated == true &&
                   context.HttpContext.User.IsInRole("Admin");
        }

        private bool IsValidApiKey(AuthorizationFilterContext context)
        {
            var expectedApiKey = _configuration["RiderManagerApiKey"];
            var actualApiKey = context.HttpContext.Request.Headers["X-API-Key"];

            return !string.IsNullOrWhiteSpace(actualApiKey) && actualApiKey == expectedApiKey;
        }

    }
}
