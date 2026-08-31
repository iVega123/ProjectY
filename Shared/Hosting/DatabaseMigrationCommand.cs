using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ProjectY.Shared.Hosting;

public static class DatabaseMigrationCommand
{
    public static async Task<bool> TryRunAsync<TContext>(
        string[] args,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        if (!args.Contains("--migrate", StringComparer.Ordinal))
        {
            return false;
        }

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await context.Database.MigrateAsync(cancellationToken);
        return true;
    }
}
