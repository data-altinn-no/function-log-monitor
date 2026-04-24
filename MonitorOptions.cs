using System.ComponentModel.DataAnnotations;

namespace FunctionLogMonitor;

public sealed class MonitorOptions
{
    [Required] public string AppInsightsAppId { get; set; } = "";
    [Required] public string AppInsightsApiKey { get; set; } = "";
    [Required] public string GitHubInputOwner { get; set; } = "";
    [Required] public string GitHubInputRepo { get; set; } = "";
    public string TriageLabel { get; set; } = "auto-triage";
    public int LookbackMinutes { get; set; } = 30;

    public void LoadFromEnvironment()
    {
        AppInsightsAppId = Env("APPINSIGHTS_APP_ID");
        AppInsightsApiKey = Env("APPINSIGHTS_API_KEY");
        GitHubInputOwner = Env("GITHUB_INPUT_OWNER");
        GitHubInputRepo = Env("GITHUB_INPUT_REPO");
        TriageLabel = EnvOr("TRIAGE_LABEL", "auto-triage");
        LookbackMinutes = int.TryParse(EnvOr("LOOKBACK_MINUTES", "30"), out var v) ? v : 30;
    }

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? "";

    private static string EnvOr(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
}
