using AuthGate.Model;
using Microsoft.AspNetCore.Identity;

namespace AuthGate.Services;

public static class AdminBootstrapper
{
    private const string AdminRole = "Admin";

    public static async Task BootstrapAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger)
    {
        var email = configuration["BootstrapAdmin:Email"];
        var password = configuration["BootstrapAdmin:Password"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "BootstrapAdmin__Email and BootstrapAdmin__Password must be set for --bootstrap-admin.");
        }

        using var scope = services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        if (!await roleManager.RoleExistsAsync(AdminRole))
        {
            EnsureSucceeded(
                await roleManager.CreateAsync(new IdentityRole(AdminRole)),
                "create the Admin role");
        }

        var user = await userManager.FindByEmailAsync(email);
        var userWasCreated = false;

        if (user is null)
        {
            user = new AdminUser { UserName = email, Email = email };
            EnsureSucceeded(
                await userManager.CreateAsync(user, password),
                "create the bootstrap administrator");
            userWasCreated = true;
        }

        if (!await userManager.IsInRoleAsync(user, AdminRole))
        {
            var roleResult = await userManager.AddToRoleAsync(user, AdminRole);
            if (!roleResult.Succeeded && userWasCreated)
            {
                await userManager.DeleteAsync(user);
            }

            EnsureSucceeded(roleResult, "assign the Admin role");
        }

        logger.LogInformation("Administrator {UserId} is ready.", user.Id);
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errors = string.Join(", ", result.Errors.Select(error => error.Description));
        throw new InvalidOperationException($"Failed to {operation}: {errors}");
    }
}
