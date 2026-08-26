using System.ComponentModel.DataAnnotations;

namespace FunctionLogMonitor;

public sealed class MonitorOptions
{
    [Required] public string AppInsightsAppId { get; set; } = "";
    [Required] public string AppInsightsApiKey { get; set; } = "";
    [Required] public string GitHubInputOwner { get; set; } = "";
    [Required] public string GitHubInputRepo { get; set; } = "";
    public string TriageLabelErrors { get; set; } = "";
    public string TriageLabelExceptions { get; set; } = "";
    public int LookbackMinutes { get; set; } = 30;

    public int CountWindowHours { get; set; } = 24;

    // 1 = no threshold, deliberately: raising it drops rare-but-real defects.
    public int MinOccurrencesExceptions { get; set; } = 1;

    public int MinOccurrencesTraces { get; set; } = 25;

    public int MaxIssuesPerRun { get; set; } = 5;
    public int MaxQueryRows { get; set; } = 25;

    public bool DryRun { get; set; }

    public void LoadFromEnvironment()
    {
        AppInsightsAppId = Env("APPINSIGHTS_APP_ID");
        AppInsightsApiKey = Env("APPINSIGHTS_API_KEY");
        GitHubInputOwner = Env("GITHUB_INPUT_OWNER");
        GitHubInputRepo = Env("GITHUB_INPUT_REPO");
        TriageLabelErrors = Env("TRIAGE_LABEL_ERRORS");
        TriageLabelExceptions = Env("TRIAGE_LABEL_EXCEPTIONS");
        LookbackMinutes = int.TryParse(EnvOr("LOOKBACK_MINUTES", "30"), out var v) ? v : 30;
        CountWindowHours = IntOr("COUNT_WINDOW_HOURS", 24);
        MinOccurrencesExceptions = IntOr("MIN_OCCURRENCES_EXCEPTIONS", 3);
        MinOccurrencesTraces = IntOr("MIN_OCCURRENCES_TRACES", 25);
        MaxIssuesPerRun = IntOr("MAX_ISSUES_PER_RUN", 5);
        MaxQueryRows = IntOr("MAX_QUERY_ROWS", 25);
        DryRun = string.Equals(EnvOr("DRY_RUN", "false"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? "";

    private static string EnvOr(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static int IntOr(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
}
