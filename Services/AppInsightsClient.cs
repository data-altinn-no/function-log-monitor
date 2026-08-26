using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace FunctionLogMonitor.Services;

public interface IAppInsightsClient
{
    Task<IReadOnlyList<ExceptionRow>> QueryExceptionsAsync(int lookbackMinutes, CancellationToken ct);
    Task<IReadOnlyList<ServerErrorRow>> QueryServerErrorsAsync(int lookbackMinutes, CancellationToken ct);
}

public sealed record ExceptionRow(
    string CloudRoleName,
    string ProblemId,
    long Count,
    string ExceptionType,
    string Message,
    string StackTrace,
    string Operation,
    string RequestPath,
    string CorrelationId,
    string FirstSeen);

public sealed record ServerErrorRow(
    string CloudRoleName,   
    long Count,
    string ExceptionType,
    string Message,
    string Operation,   
    string CorrelationId,
    string FirstSeen, 
    string Severity,
    string Template);

/// <summary>
/// Minimal App Insights REST query client.
/// In prod, prefer <c>Azure.Monitor.Query</c> + <c>DefaultAzureCredential</c>.
/// </summary>
public sealed class AppInsightsClient : IAppInsightsClient
{
    private const string KqlTemplateExceptions = """
        let MinOcc = {MIN_OCCURRENCES_EXC};
        exceptions
        | where timestamp > ago({LOOKBACK}m)
        | where cloud_RoleName startswith "func"
        // startswith "func" alone admits staging slots.
        | where cloud_RoleName !endswith "-staging"
            and cloud_RoleName !has "-test" and cloud_RoleName !has "-dev"
        | where isempty(operation_SyntheticSource)
        | summarize
            // count() undercounts: sampling is on in host.json.
            count_ = sum(coalesce(toint(itemCount), 1)),
            // any() would re-roll the sampled blob, and the fingerprint, per poll.
            sampleException = take_any(pack(
                "type", type,
                "message", outerMessage,
                "details", tostring(details),
                "operation", operation_Name,
                "requestPath", tostring(customDimensions["RequestPath"]),
                "correlationId", operation_Id,
                "timestamp", timestamp
            ))
            by cloud_RoleName, problemId
        | project
            cloud_RoleName,
            problemId,
            count_,
            exceptionType = tostring(sampleException["type"]),
            message       = tostring(sampleException["message"]),
            stackTrace    = tostring(sampleException["details"]),
            operation     = tostring(sampleException["operation"]),
            requestPath   = tostring(sampleException["requestPath"]),
            correlationId = tostring(sampleException["correlationId"]),
            firstSeen     = todatetime(sampleException["timestamp"])
        // Window is LOOKBACK, not CountWindow: N means N times in one poll.
        | where count_ >= MinOcc
        | order by count_ desc
        """;

    // The raw message embeds per-request ids, so grouping on it buckets every
    // occurrence separately.
    private const string KqlTemplateServerErrors = """
        let CountWindow = {COUNT_WINDOW}h;
        let Lookback    = {LOOKBACK}m;
        let MinOcc      = {MIN_OCCURRENCES};
        let MaxRows     = {MAX_ROWS};
        traces
        | where timestamp > ago(CountWindow)
        | where cloud_RoleName startswith "func"
        // startswith "func" alone admits staging slots.
        | where cloud_RoleName !endswith "-staging"
            and cloud_RoleName !has "-test" and cloud_RoleName !has "-dev"
        | where severityLevel > 2
        | extend Category = tostring(customDimensions["Category"])
        // Emitted alongside the underlying exception; keeping it double-counts.
        | where message !startswith "Executed '"
        | where message !startswith "[HostMonitor]"
        | where message !startswith "[Tag="
        | where Category !startswith "Host."
        | where not(message matches regex @"(?i)(timeout when fetching|was cancell?ed"
                 @"|invalid_grant|jwt has expired|no such host"
                 @"|connection (attempt failed|refused|reset)|HttpClient\\.Timeout of"
                 @"|Language Worker Process exited|Failed to start language worker"
                 @"|dotnet\\.exe exited with code|Hosting failed to start"
                 @"|A host error has occurred)")
        | extend Template = tostring(customDimensions["prop__{OriginalFormat}"])
        | extend Template = iff(isempty(Template),
              replace_regex(replace_regex(replace_regex(message,
                @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<guid>"),
                @"\\b[0-9a-fA-F]{16,}\\b", "<hex>"),
                @"\\d+", "<n>"),
              Template)
        | summarize
            // count() undercounts: sampling is on in host.json.
            count_    = sum(coalesce(toint(itemCount), 1)),
            firstSeen = min(timestamp),
            lastSeen  = max(timestamp),
            // any() is non-deterministic; the fingerprint would change per poll.
            (sampleTime, sampleMessage, sampleOperation, sampleCorrelationId, sampleSeverity)
              = arg_max(timestamp, message,
                    iff(operation_Name != "", operation_Name,
                        tostring(customDimensions["AzureFunctions_FunctionName"])),
                    operation_Id, severityLevel)
            by cloud_RoleName, Template
        | where count_ >= MinOcc
        // Makes a replayed or late timer run idempotent.
        | where lastSeen > ago(Lookback)
        | project
            cloud_RoleName,
            count_,
            exceptionType = "trace",
            message       = sampleMessage,
            template      = Template,
            operation     = tostring(sampleOperation),
            correlationId = tostring(sampleCorrelationId),
            firstSeen     = todatetime(firstSeen),
            severity      = tostring(sampleSeverity)
        | order by count_ desc
        | take MaxRows
        """;

    private readonly HttpClient _http;
    private readonly MonitorOptions _opts;

    public AppInsightsClient(HttpClient http, IOptions<MonitorOptions> opts)
    {
        _http = http;
        _opts = opts.Value;
    }

    public async Task<IReadOnlyList<ExceptionRow>> QueryExceptionsAsync(
        int lookbackMinutes, CancellationToken ct)
    {
        var url = $"https://api.applicationinsights.io/v1/apps/{_opts.AppInsightsAppId}/query";
        var kql = KqlTemplateExceptions
            .Replace("{LOOKBACK}", lookbackMinutes.ToString())
            .Replace("{MIN_OCCURRENCES_EXC}", _opts.MinOccurrencesExceptions.ToString());

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-api-key", _opts.AppInsightsApiKey);
        req.Content = JsonContent.Create(new { query = kql });

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var result = await resp.Content.ReadFromJsonAsync<QueryResult>(cancellationToken: ct);
        var table = result?.Tables?.FirstOrDefault();
        if (table is null) return Array.Empty<ExceptionRow>();

        var columns = table.Columns.Select(c => c.Name).ToArray();
        return table.Rows.Select(row => MapExceptionRow(columns, row)).ToArray();
    }

    public async Task<IReadOnlyList<ServerErrorRow>> QueryServerErrorsAsync(
        int lookbackMinutes, CancellationToken ct)
    {
        var url = $"https://api.applicationinsights.io/v1/apps/{_opts.AppInsightsAppId}/query";
        var kql = KqlTemplateServerErrors
            .Replace("{LOOKBACK}", lookbackMinutes.ToString())
            .Replace("{COUNT_WINDOW}", _opts.CountWindowHours.ToString())
            .Replace("{MIN_OCCURRENCES}", _opts.MinOccurrencesTraces.ToString())
            .Replace("{MAX_ROWS}", _opts.MaxQueryRows.ToString());

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-api-key", _opts.AppInsightsApiKey);
        req.Content = JsonContent.Create(new { query = kql });

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var result = await resp.Content.ReadFromJsonAsync<QueryResult>(cancellationToken: ct);
        var table = result?.Tables?.FirstOrDefault();
        if (table is null) return Array.Empty<ServerErrorRow>();

        var columns = table.Columns.Select(c => c.Name).ToArray();
        return table.Rows.Select(row => MapErrorRow(columns, row)).ToArray();
    }
        
    private static ExceptionRow MapExceptionRow(string[] columns, JsonElement[] row)
    {
        string S(string col)
        {
            var idx = Array.IndexOf(columns, col);
            if (idx < 0) return "";
            var v = row[idx];
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => v.ToString()
            };
        }

        long L(string col)
        {
            var idx = Array.IndexOf(columns, col);
            if (idx < 0) return 0;
            var v = row[idx];
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        }

        return new ExceptionRow(
            CloudRoleName: S("cloud_RoleName"),
            ProblemId: S("problemId"),
            Count: L("count_"),
            ExceptionType: S("exceptionType"),
            Message: S("message"),
            StackTrace: S("stackTrace"),
            Operation: S("operation"),
            RequestPath: S("requestPath"),
            CorrelationId: S("correlationId"),
            FirstSeen: S("firstSeen"));
    }

    private static ServerErrorRow MapErrorRow(string[] columns, JsonElement[] row)
    {
        string S(string col)
        {
            var idx = Array.IndexOf(columns, col);
            if (idx < 0) return "";
            var v = row[idx];
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => v.ToString()
            };
        }

        long L(string col)
        {
            var idx = Array.IndexOf(columns, col);
            if (idx < 0) return 0;
            var v = row[idx];
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        }

        return new ServerErrorRow(
            CloudRoleName: S("cloud_RoleName"),           
            Count: L("count_"),
            ExceptionType: S("exceptionType"),
            Message: S("message"),          
            Operation: S("operation"),           
            CorrelationId: S("correlationId"),
            FirstSeen: S("firstSeen"),
            Severity: S("severity"),
            Template: S("template"));
    }

    private sealed record QueryResult(
        [property: JsonPropertyName("tables")] Table[]? Tables);

    private sealed record Table(
        [property: JsonPropertyName("columns")] Column[] Columns,
        [property: JsonPropertyName("rows")] JsonElement[][] Rows);

    private sealed record Column(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string? Type);
}
