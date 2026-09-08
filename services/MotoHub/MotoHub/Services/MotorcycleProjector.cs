using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using Npgsql;

namespace MotoHub.Services;

/// <summary>What a pass moved, and which rows the target schema would not take.</summary>
public sealed record ProjectionResult(int Applied, IReadOnlyList<string> Rejected);

/// <summary>
/// Copies motorcycles into the target schema's <c>motorcycles</c> table.
///
/// rentals.motorcycle_id carries a foreign key to this table, so no rental can
/// move to the target engine until the motorcycle it references is there. That
/// makes this the first move of the migration rather than a part of it.
///
/// It reconciles rather than dual-writes on purpose. Nothing reads the target
/// table yet, so lag costs nothing, while a synchronous second write would put
/// a brand new datastore in the path of registering a motorcycle -- buying an
/// availability risk to solve a problem that does not exist yet. Reconciling is
/// also self-healing: a row missed during an outage is picked up on the next
/// pass instead of needing a repair job.
/// </summary>
public sealed class MotorcycleProjector(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<MotorcycleProjector> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var target = config.GetConnectionString("TargetSchema");
        if (string.IsNullOrWhiteSpace(target)) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var result = await ProjectAsync(
                    scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), target, token);
                log.LogDebug("Projected {Count} motorcycles into the target schema", result.Applied);
                // Loud, and every pass. A row the target schema will not take is a row
                // whose rentals cannot move either, so this must not decay into a line
                // that was logged once at startup and scrolled away.
                if (result.Rejected.Count > 0)
                    log.LogError("Target schema rejected {Count} motorcycles: {Plates}",
                        result.Rejected.Count, string.Join(", ", result.Rejected));
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                log.LogWarning(error, "Motorcycle projection delayed; retrying on the next pass");
            }
            await Task.Delay(Interval, token);
        }
    }

    public static async Task<ProjectionResult> ProjectAsync(
        ApplicationDbContext source, string target, CancellationToken token)
    {
        var motorcycles = await source.Motorcycles.AsNoTracking().ToListAsync(token);
        if (motorcycles.Count == 0) return new ProjectionResult(0, []);

        await using var dataSource = new NpgsqlDataSourceBuilder(target).Build();
        await using var connection = await dataSource.OpenConnectionAsync(token);
        var applied = 0;
        var rejected = new List<string>();
        foreach (var motorcycle in motorcycles)
        {
            // A row the target schema refuses -- a year outside its CHECK, an id that
            // is not a UUID -- must not stop the rows behind it. It is reported and
            // skipped, because the migration needs to know which rows disagree with
            // the schema, and a projector that dies on the first one never finds out.
            if (!Guid.TryParse(motorcycle.Id, out var id))
            {
                rejected.Add($"{motorcycle.LicensePlate} (id {motorcycle.Id} is not a UUID)");
                continue;
            }

            await using var command = new NpgsqlCommand("""
                INSERT INTO motorcycles (id, license_plate, model, year, registered_at, retired_at)
                VALUES (@id, @plate, @model, @year, @registered, @retired)
                ON CONFLICT (id) DO UPDATE SET
                    license_plate = EXCLUDED.license_plate,
                    model = EXCLUDED.model,
                    year = EXCLUDED.year,
                    registered_at = EXCLUDED.registered_at,
                    retired_at = EXCLUDED.retired_at
                """, connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("plate", motorcycle.LicensePlate);
            command.Parameters.AddWithValue("model", (object?)motorcycle.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("year", motorcycle.Year);
            command.Parameters.AddWithValue("registered", DateTime.SpecifyKind(motorcycle.RegistrationDate, DateTimeKind.Utc));
            command.Parameters.AddWithValue("retired", motorcycle.RetiredAtUtc is { } retired
                ? DateTime.SpecifyKind(retired, DateTimeKind.Utc)
                : DBNull.Value);
            try
            {
                applied += await command.ExecuteNonQueryAsync(token);
            }
            catch (PostgresException refused) when (refused.SqlState is
                PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation)
            {
                rejected.Add($"{motorcycle.LicensePlate} ({refused.SqlState})");
            }
        }
        return new ProjectionResult(applied, rejected);
    }
}
