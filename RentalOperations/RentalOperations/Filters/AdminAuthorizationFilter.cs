using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;

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
            if (context.HttpContext.User.IsInRole("Admin"))
            {
                return;
            }

            var expectedApiKey = _configuration["RentalOperationsApiKey"];
            var actualApiKey = context.HttpContext.Request.Headers["X-API-Key"];
            if (!string.IsNullOrWhiteSpace(actualApiKey) && actualApiKey == expectedApiKey)
            {
                return;
            }

            if (context.HttpContext.User.Identity?.IsAuthenticated == true)
            {
                context.Result = new ForbidResult();
                return;
            }

            context.Result = new UnauthorizedResult();
        }
    }
}
