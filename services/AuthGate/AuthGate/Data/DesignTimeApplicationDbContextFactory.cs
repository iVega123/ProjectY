using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthGate.Data;

public sealed class DesignTimeApplicationDbContextFactory
    : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var databaseName = Environment.GetEnvironmentVariable("AUTH_GATE_POSTGRES_DB") ?? "AuthGateDB";
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgresql")
            ?? $"Host=localhost;Database={databaseName};Username=postgres";
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }
}
