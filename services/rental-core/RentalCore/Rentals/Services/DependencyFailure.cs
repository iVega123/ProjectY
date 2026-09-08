using System.Diagnostics;
using System.Diagnostics.Metrics;
using Npgsql;

namespace RentalOperations.Services;

public static class DependencyFailure
{
    private static readonly Meter Meter = new("ProjectY.Resilience");
    private static readonly Counter<long> Refusals = Meter.CreateCounter<long>("dependency.refusals");

    public static bool IsUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or TaskCanceledException) { return true; }
            if (IsDatabaseUnavailable(current)) { return true; }
            if (current is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500))
            { return true; }
        }
        return false;
    }

    /// <summary>
    /// Indisponibilidade do banco não é qualquer erro vindo do banco.
    ///
    /// Uma violação de unicidade é o índice parcial fazendo o trabalho dele: a
    /// resposta certa é 409, não 503, e tratá-la como indisponibilidade faria o
    /// cliente repetir uma requisição que será recusada de novo. Por isso a
    /// separação é entre "o servidor respondeu" e "não deu para falar com ele",
    /// mais os poucos SQLSTATEs em que o servidor responde justamente para
    /// dizer que não pode atender agora.
    /// </summary>
    private static bool IsDatabaseUnavailable(Exception exception) => exception switch
    {
        PostgresException postgres => postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
            || postgres.SqlState.StartsWith("53", StringComparison.Ordinal)
            || postgres.SqlState.StartsWith("57", StringComparison.Ordinal)
            || postgres.SqlState == PostgresErrorCodes.SerializationFailure
            || postgres.SqlState == PostgresErrorCodes.DeadlockDetected,
        NpgsqlException => true,
        _ => false
    };

    public static void Record(Exception exception)
    {
        var dependency = "upstream";
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException)
            {
                dependency = "database";
                break;
            }
        }
        Refusals.Add(1, new KeyValuePair<string, object?>("dependency", dependency));
        Activity.Current?.SetTag("projecty.degradation", dependency);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, "Dependency unavailable");
    }
}
