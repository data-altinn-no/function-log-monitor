using Microsoft.Extensions.Logging;

namespace FunctionLogMonitor;

public static class WorkerLogging
{
    private const string ApplicationInsightsProvider =
        "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider";

    // AddApplicationInsightsTelemetryWorkerService installs a Warning-level rule,
    // so without this every LogInformation from the worker is dropped silently.
    public static void RemoveApplicationInsightsWarningFilter(LoggerFilterOptions options)
    {
        var defaultRule = options.Rules.FirstOrDefault(
            rule => rule.ProviderName == ApplicationInsightsProvider);
        if (defaultRule is not null)
        {
            options.Rules.Remove(defaultRule);
        }
    }
}
