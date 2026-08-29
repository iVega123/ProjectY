using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;

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
            var expectedApiKey = _configuration["RentalOperationsApiKey"];
            var actualApiKey = context.HttpContext.Request.Headers["X-API-Key"];
            if (!string.IsNullOrWhiteSpace(actualApiKey) && actualApiKey == expectedApiKey)
            {
                return;
            }

            if (context.HttpContext.User.Identity?.IsAuthenticated == true)
            {
                if (context.HttpContext.User.IsInRole("Admin") || context.HttpContext.User.IsInRole("Rider"))
                {
                    return;
                }

                context.Result = new ForbidResult();
                return;
            }

            context.Result = new UnauthorizedResult();
        }
    }
}
