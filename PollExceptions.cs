using FunctionLogMonitor.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FunctionLogMonitor;

public sealed class PollExceptions
{
    private const string IssueBodyTemplate = """
        ### Exception
        {0}

        ### Message
        {1}

        ### Cloud role
        {2}

        ### Operation
        {3}

        ### Request path
        {4}

        ### Timestamp
        {5}

        ### Correlation id
        {6}

        ### Occurrences
        {7}

        ### Fingerprint
        {8}

        ### Stack trace
        ```
        {9}
        ```
        """;

    private readonly IAppInsightsClient _appInsights;
    private readonly IGitHubIssueWriter _github;
    private readonly IRedactor _redactor;
    private readonly MonitorOptions _opts;
    private readonly ILogger<PollExceptions> _log;

    public PollExceptions(
        IAppInsightsClient appInsights,
        IGitHubIssueWriter github,
        IRedactor redactor,
        IOptions<MonitorOptions> opts,
        ILogger<PollExceptions> log)
    {
        _appInsights = appInsights;
        _github = github;
        _redactor = redactor;
        _opts = opts.Value;
        _log = log;
    }

    [Function("PollExceptions")]
    public async Task RunAsync(
        [TimerTrigger("0 */30 * * * *", RunOnStartup = false)] TimerInfo timer,
        CancellationToken ct)
    {
        var lookback = _opts.LookbackMinutes;
        _log.LogInformation("poll.start lookback={Lookback}m", lookback);

        var rows = await _appInsights.QueryExceptionsAsync(lookback, ct);
        if (rows.Count == 0)
        {
            _log.LogInformation("poll.no_rows");
            return;
        }

        var existing = new HashSet<string>(
            await _github.GetRecentFingerprintsAsync(ct),
            StringComparer.Ordinal);

        var created = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var excType = _redactor.Redact(string.IsNullOrEmpty(row.ExceptionType) ? "UnknownException" : row.ExceptionType);
            var stack = _redactor.Redact(row.StackTrace);
            var fingerprint = Fingerprint.Compute(excType, stack);

            if (existing.Contains(fingerprint)) continue;

            var body = string.Format(
                IssueBodyTemplate,
                excType,
                _redactor.Redact(row.Message),
                string.IsNullOrEmpty(row.CloudRoleName) ? "unknown" : row.CloudRoleName,
                _redactor.Redact(row.Operation),
                _redactor.Redact(row.RequestPath),
                row.FirstSeen,
                row.CorrelationId,
                row.Count > 0 ? row.Count : 1,
                $"{GitHubIssueWriter.FingerprintMarker} {fingerprint} -->",
                Truncate(stack, 8000));

            _redactor.AssertClean(body);

            var title = Truncate(
                $"[prod] {excType} in {(string.IsNullOrEmpty(row.CloudRoleName) ? "unknown" : row.CloudRoleName)}",
                200);

            await _github.CreateIssueAsync(
                title,
                body,
                new[] { _opts.TriageLabel, "prod", "source:app-insights" },
                ct);

            existing.Add(fingerprint);
            created++;
        }

        _log.LogInformation("poll.done created={Created} total_rows={Total}", created, rows.Count);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
