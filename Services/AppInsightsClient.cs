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
    string Severity);

/// <summary>
/// Minimal App Insights REST query client.
/// In prod, prefer <c>Azure.Monitor.Query</c> + <c>DefaultAzureCredential</c>.
/// </summary>
public sealed class AppInsightsClient : IAppInsightsClient
{
    private const string KqlTemplateExceptions = """
        exceptions
        | where timestamp > ago({LOOKBACK}m)
        | where cloud_RoleName startswith "func"
        | summarize
            count_ = count(),
            sampleException = any(pack(
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
        | order by count_ desc
        """;

    private const string KqlTemplateServerErrors = """
        traces
        | where timestamp > ago({LOOKBACK}m)
        | where cloud_RoleName startswith "func" 
        | where severityLevel > 2
        | summarize
            count_ = count(),
            sampleException = any(pack(
                "type", itemType,
                "message", message,                              
                "operation", iff(operation_Name != "", operation_Name, tostring(customDimensions["AzureFunctions_FunctionName"])),              
                "correlationId", operation_Id,
                "timestamp", timestamp,
                "severity", severityLevel
            ))
            by cloud_RoleName, message
        | project
            cloud_RoleName,      
            count_,
            exceptionType = tostring(sampleException["type"]),
            message       = tostring(sampleException["message"]),         
            operation     = tostring(sampleException["operation"]),            
            correlationId = tostring(sampleException["correlationId"]),
            firstSeen     = todatetime(sampleException["timestamp"]),
            severity      = tostring(sampleException["severity"])
        | order by count_ desc
        
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
        var kql = KqlTemplateExceptions.Replace("{LOOKBACK}", lookbackMinutes.ToString());

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
        var kql = KqlTemplateServerErrors.Replace("{LOOKBACK}", lookbackMinutes.ToString());

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
            Severity: S("severity"));
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
