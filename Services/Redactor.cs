using System.Text.RegularExpressions;

namespace FunctionLogMonitor.Services;

public interface IRedactor
{
    string Redact(string? text);
    void AssertClean(string text);
}

/// <summary>
/// Regex-based redaction of PII / secrets. Mirrors the Python reference in infra/redaction.md.
/// Keep rules in sync across the agent's Python redactor and this one.
/// </summary>
public sealed partial class Redactor : IRedactor
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    {
        // JWT first (before generic base64) to keep the label useful.
        (new Regex(@"eyJ[A-Za-z0-9_\-]+?\.[A-Za-z0-9_\-]+?\.[A-Za-z0-9_\-]+", RegexOptions.Compiled), "<jwt>"),
        (new Regex(@"Bearer\s+[A-Za-z0-9._\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase), "Bearer <tok>"),
        (new Regex(@"Basic\s+[A-Za-z0-9+/=]+", RegexOptions.Compiled | RegexOptions.IgnoreCase), "Basic <tok>"),
        // Connection-string-style key=value. Use a backreference to preserve the key name.
        (new Regex(@"(?i)(Server|Data Source|Password|Pwd|User Id|Uid|AccountKey)=[^;""'\s]+", RegexOptions.Compiled), "$1=<redacted>"),
        // Query-string secrets.
        (new Regex(@"(?i)([?&](?:api[_-]?key|token|access_token|sig|code)=)[^&#\s""']+", RegexOptions.Compiled), "$1<redacted>"),
        // Email.
        (new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled), "<email>"),
        // Norwegian fnr / d-number (11 digits).
        (new Regex(@"\b\d{11}\b", RegexOptions.Compiled), "<fnr>"),
        // Norwegian phone.
        (new Regex(@"\b(?:\+?47[\s-]?)?(?:\d{2}[\s-]?){3,4}\d{2}\b", RegexOptions.Compiled), "<phone>"),
        // IPv4.
        (new Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled), "<ip>"),
        // Long base64 blob (likely storage key / secret). Run LAST.
        (new Regex(@"[A-Za-z0-9+/]{80,}={0,2}", RegexOptions.Compiled), "<b64-redact>"),
    };

    private static readonly Regex[] DangerPatterns =
    {
        new(@"eyJ[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled),
        new(@"\b\d{11}\b", RegexOptions.Compiled),
        new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled),
    };

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var output = text;
        foreach (var (pattern, replacement) in Rules)
        {
            output = pattern.Replace(output, replacement);
        }
        return output;
    }

    /// <summary>Belt-and-suspenders: throw if a known sensitive pattern slipped through.</summary>
    public void AssertClean(string text)
    {
        foreach (var pattern in DangerPatterns)
        {
            if (pattern.IsMatch(text))
            {
                throw new InvalidOperationException(
                    $"Redaction assertion failed: pattern {pattern} still present.");
            }
        }
    }
}
