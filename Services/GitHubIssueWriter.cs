using Microsoft.Extensions.Options;
using Octokit;

namespace FunctionLogMonitor.Services;

public interface IGitHubIssueWriter
{
    Task<IReadOnlySet<string>> GetRecentFingerprintsAsync(CancellationToken ct);
    Task CreateIssueAsync(string title, string body, IEnumerable<string> labels, CancellationToken ct);
}

public sealed class GitHubIssueWriter : IGitHubIssueWriter
{
    public const string FingerprintMarker = "<!-- fingerprint:";
    private const string MarkerClose = "-->";
    private const int FingerprintScanLimit = 500;

    private readonly IGitHubClient _github;
    private readonly MonitorOptions _opts;

    public GitHubIssueWriter(IGitHubClient github, IOptions<MonitorOptions> opts)
    {
        _github = github;
        _opts = opts.Value;
    }

    public async Task<IReadOnlySet<string>> GetRecentFingerprintsAsync(CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.All,
        };
        request.Labels.Add(_opts.TriageLabel);

        var options = new ApiOptions { PageSize = 100, PageCount = 5 };
        var issues = await _github.Issue.GetAllForRepository(
            _opts.GitHubInputOwner, _opts.GitHubInputRepo, request, options);

        foreach (var issue in issues)
        {
            if (ct.IsCancellationRequested) break;
            var fp = ExtractFingerprint(issue.Body);
            if (fp is not null) seen.Add(fp);
            if (seen.Count > FingerprintScanLimit) break;
        }

        return seen;
    }

    public async Task CreateIssueAsync(
        string title, string body, IEnumerable<string> labels, CancellationToken ct)
    {
        var req = new NewIssue(title) { Body = body };
        foreach (var label in labels) req.Labels.Add(label);
        await _github.Issue.Create(_opts.GitHubInputOwner, _opts.GitHubInputRepo, req);
    }

    internal static string? ExtractFingerprint(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var start = body.IndexOf(FingerprintMarker, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = body.IndexOf(MarkerClose, start, StringComparison.Ordinal);
        if (end < 0) return null;
        var fp = body[(start + FingerprintMarker.Length)..end].Trim();
        return fp.Length == 0 ? null : fp;
    }
}
