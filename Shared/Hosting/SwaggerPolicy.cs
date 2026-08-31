using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ProjectY.Shared.Hosting;

public static class SwaggerPolicy
{
    public static bool IsEnabled(
        IHostEnvironment environment,
        IConfiguration configuration) =>
        environment.IsDevelopment() &&
        configuration.GetValue<bool>("Swagger:Enabled");
}
