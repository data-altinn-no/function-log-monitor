using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FunctionLogMonitor.Services;

/// <summary>
/// Stable error fingerprinting: strip GUIDs + numbers from the stack, SHA-256, take 16 hex chars.
/// Mirrors the Python implementation so fingerprints are interchangeable across services.
/// </summary>
public static class Fingerprint
{
    private static readonly Regex GuidRe = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private static readonly Regex NumberRe = new(@"\b\d+\b", RegexOptions.Compiled);

    public static string Compute(string? exceptionType, string? stackTrace)
    {
        var norm = GuidRe.Replace(stackTrace ?? "", "<guid>");
        norm = NumberRe.Replace(norm, "<n>").Trim();
        var input = $"{exceptionType ?? ""}\n{norm}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return hex[..16];
    }
}
