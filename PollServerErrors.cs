using FunctionLogMonitor.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FunctionLogMonitor;

public sealed class PollServerErrors
{
    private const string IssueBodyTemplate = """
        ### Error
        {0}

        ### Message
        {1}

        ### Cloud role
        {2}

        ### Operation
        {3}

        ### Timestamp
        {4}

        ### Correlation id
        {5}

        ### Occurrences
        {6}

        ### Fingerprint
        {7}

        ### Stack trace
        ```
        {8}
        ```
        """;

    private readonly IAppInsightsClient _appInsights;
    private readonly IGitHubIssueWriter _github;
    private readonly IRedactor _redactor;
    private readonly MonitorOptions _opts;
    private readonly ILogger<PollServerErrors> _log;

    public PollServerErrors(
        IAppInsightsClient appInsights,
        IGitHubIssueWriter github,
        IRedactor redactor,
        IOptions<MonitorOptions> opts,
        ILogger<PollServerErrors> log)
    {
        _appInsights = appInsights;
        _github = github;
        _redactor = redactor;
        _opts = opts.Value;
        _log = log;
    }

    [Function("PollServerErrors")]
    public async Task RunAsync(
        [TimerTrigger("0 */30 * * * *", RunOnStartup = false)] TimerInfo timer,
        CancellationToken ct)
    {
        var lookback = _opts.LookbackMinutes;
        _log.LogInformation("poll.start lookback={Lookback}m", lookback);

        var rows = await _appInsights.QueryServerErrorsAsync(lookback, ct);
        if (rows.Count == 0)
        {
            _log.LogInformation("poll.no_rows");
            return;
        }

        var existing = new HashSet<string>(
            await _github.GetRecentFingerprintsAsync(ct, _opts.TriageLabelErrors),
            StringComparer.Ordinal);

        var created = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (created >= _opts.MaxIssuesPerRun)
            {
                _log.LogWarning("poll.run_cap_reached cap={Cap} remaining={Remaining}",
                    _opts.MaxIssuesPerRun, rows.Count - created);
                break;
            }

            var excType = string.IsNullOrEmpty(row.ExceptionType) ? "UnknownException" : row.ExceptionType;

            var fingerprint = Fingerprint.ComputeFromTemplate(row.CloudRoleName, row.Template);

            if (existing.Contains(fingerprint)) continue;

            var stack = _redactor.Redact(row.Message);

            var body = string.Format(
                IssueBodyTemplate,
                excType,
                _redactor.Redact(row.Message),
                string.IsNullOrEmpty(row.CloudRoleName) ? "unknown" : row.CloudRoleName,
                _redactor.Redact(row.Operation),               
                row.FirstSeen,
                row.CorrelationId,
                row.Count > 0 ? row.Count : 1,
                $"{GitHubIssueWriter.FingerprintMarker} {fingerprint} -->",
                Truncate(stack, 8000));

            _redactor.AssertClean(body);

            if (_opts.DryRun)
            {
                _log.LogInformation(
                    "poll.would_create fingerprint={Fingerprint} occurrences={Count} role={Role} template={Template}",
                    fingerprint, row.Count, row.CloudRoleName, Truncate(row.Template, 200));
                existing.Add(fingerprint);
                created++;
                continue;
            }

            var title = Truncate(
                $"[prod] {excType} in {(string.IsNullOrEmpty(row.CloudRoleName) ? "unknown" : row.CloudRoleName)}",
                200);

            await _github.CreateIssueAsync(
                title,
                body,
                new[] { _opts.TriageLabelErrors, "prod", "source:app-insights" },
                ct);

            existing.Add(fingerprint);
            created++;
        }

        _log.LogInformation("poll.done created={Created} total_rows={Total}", created, rows.Count);
    }

    [Function("DebugServerErrors")]
    public async Task RunAsyncDebug([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequestData req,
            FunctionContext context)
    {
        await RunAsync(new TimerInfo(), new CancellationToken());
    }


    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
