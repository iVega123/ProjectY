using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MotoHub.Data;

public sealed class DesignTimeApplicationDbContextFactory
    : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var databaseName = Environment.GetEnvironmentVariable("MOTO_HUB_POSTGRES_DB") ?? "MotoHubDB";
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgresql")
            ?? $"Host=localhost;Database={databaseName};Username=postgres";
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }
}
