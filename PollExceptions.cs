using FunctionLogMonitor.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
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

        ### Top frame
        ```
        {10}
        ```

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
            await _github.GetRecentFingerprintsAsync(ct, _opts.TriageLabelExceptions),
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

            if (ExceptionClassifier.ShouldSkip(excType, row.Message)) continue;

            var fingerprint = Fingerprint.Compute(excType, row.StackTrace);

            if (existing.Contains(fingerprint)) continue;

            excType = _redactor.Redact(excType);

            var flattened = StackFlattener.Flatten(row.StackTrace);
            var stack = _redactor.Redact(
                string.IsNullOrEmpty(flattened) ? row.StackTrace : flattened);
            var top = StackFlattener.TopFirstPartyFrame(row.StackTrace);
            var topFrame = top is null ? "(no first-party frame)" : StackFlattener.Format(top.Value);

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
                Truncate(stack, 8000),
                _redactor.Redact(topFrame));

            _redactor.AssertClean(body);

            if (_opts.DryRun)
            {
                _log.LogInformation(
                    "poll.would_create fingerprint={Fingerprint} occurrences={Count} type={Type} role={Role}",
                    fingerprint, row.Count, excType, row.CloudRoleName);
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
                new[] { _opts.TriageLabelExceptions, "prod", "source:app-insights" },
                ct);

            existing.Add(fingerprint);
            created++;
        }

        _log.LogInformation("poll.done created={Created} total_rows={Total}", created, rows.Count);
    }

    [Function("DebugPollExceptions")]
    public async Task RunAsyncDebug([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequestData req,
            FunctionContext context)
    {
        await RunAsync(new TimerInfo(), new CancellationToken());
    }


    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
