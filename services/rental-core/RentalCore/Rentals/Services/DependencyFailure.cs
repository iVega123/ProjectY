using MongoDB.Driver;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RentalOperations.Services;

public static class DependencyFailure
{
    private static readonly Meter Meter = new("ProjectY.Resilience");
    private static readonly Counter<long> Refusals = Meter.CreateCounter<long>("dependency.refusals");

    public static bool IsUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is MongoException or TimeoutException or TaskCanceledException) { return true; }
            if (current is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500))
            { return true; }
        }
        return false;
    }

    public static void Record(Exception exception)
    {
        var dependency = "upstream";
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is MongoException)
            {
                dependency = "mongodb";
                break;
            }
        }
        Refusals.Add(1, new KeyValuePair<string, object?>("dependency", dependency));
        Activity.Current?.SetTag("projecty.degradation", dependency);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, "Dependency unavailable");
    }
}
