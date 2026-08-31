using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ProjectY.Shared.Hosting;

public static class DatabaseMigrationCommand
{
    public const string Argument = "--migrate=true";

    public static bool IsRequested(IEnumerable<string> args) =>
        args.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    public static async Task<bool> TryRunAsync<TContext>(
        string[] args,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        if (!IsRequested(args))
        {
            return false;
        }

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await context.Database.MigrateAsync(cancellationToken);
        return true;
    }
}
