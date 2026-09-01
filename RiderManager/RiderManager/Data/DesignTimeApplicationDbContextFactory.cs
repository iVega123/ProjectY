using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RiderManager.Data;

public sealed class DesignTimeApplicationDbContextFactory
    : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var databaseName = Environment.GetEnvironmentVariable("RIDER_MANAGER_POSTGRES_DB") ?? "RiderManagerDB";
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgresql")
            ?? $"Host=localhost;Database={databaseName};Username=postgres";
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }
}
