using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FunctionLogMonitor.Services;

public static class Fingerprint
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    {
        (new Regex(@"\b00-[0-9a-fA-F]{32}-[0-9a-fA-F]{16}-[0-9a-fA-F]{2}\b", RegexOptions.Compiled), "<traceparent>"),
        (new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled), "<guid>"),
        (new Regex(@"\b[0-9a-fA-F]{16,}\b", RegexOptions.Compiled), "<hex>"),
        (new Regex(@"\b0x[0-9a-fA-F]+\b", RegexOptions.Compiled), "<hex>"),
        (new Regex(@"\?[^\s""']*", RegexOptions.Compiled), "?<query>"),
        (new Regex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?Z?", RegexOptions.Compiled), "<ts>"),
        (new Regex(@":line \d+", RegexOptions.Compiled | RegexOptions.IgnoreCase), ":line <n>"),
        (new Regex(@"Version=[\d\.]+", RegexOptions.Compiled), "Version=<v>"),
        // Must run last, or it destroys the patterns above.
        (new Regex(@"\b\d+\b", RegexOptions.Compiled), "<n>"),
        (new Regex(@"\s+", RegexOptions.Compiled), " "),
    };

    private static string Normalize(string? text)
    {
        var output = text ?? "";
        foreach (var (pattern, replacement) in Rules)
            output = pattern.Replace(output, replacement);
        return output.Trim();
    }

    private const int SignatureFrames = 5;

    // Compiler-generated names carry ordinals that are renumbered on rebuild.
    private static readonly (Regex Pattern, string Replacement)[] MethodRules =
    {
        (new Regex(@"<(\w+)>g__(\w+)\|\d+_\d+", RegexOptions.Compiled), "$1.$2"),
        (new Regex(@"<(\w+)>[a-z]__\d+(_\d+)?", RegexOptions.Compiled), "$1"),
        (new Regex(@"<>c(__DisplayClass\d+_\d+)?", RegexOptions.Compiled), ""),
        (new Regex(@"\.MoveNext$", RegexOptions.Compiled), ""),
        (new Regex(@"`\d+", RegexOptions.Compiled), ""),
        (new Regex(@"[+.]+", RegexOptions.Compiled), "."),
    };

    public static string NormalizeMethod(string method)
    {
        var output = method;
        foreach (var (pattern, replacement) in MethodRules)
            output = pattern.Replace(output, replacement);
        return output.Trim('.');
    }

    /// <summary>Type plus first-party method names, so a redeploy does not re-file the same error.</summary>
    public static string ComputeFromFrames(string? exceptionType, IEnumerable<string> methods)
    {
        // An outer and inner exception share the call path, so a method can recur.
        var signature = methods
            .Select(NormalizeMethod)
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(SignatureFrames);
        return Hash($"frames\n{exceptionType ?? ""}\n{string.Join("\n", signature)}");
    }

    private static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
               .ToLowerInvariant()[..16];

    // Template, not the rendered message. Both overloads need raw, unredacted
    // text: Redactor splits GUIDs and versions, fragmenting the signature.
    public static string ComputeFromTemplate(string? cloudRoleName, string? template) =>
        Hash($"trace\n{cloudRoleName ?? ""}\n{Normalize(template)}");

    public static string Compute(string? exceptionType, string? stackTrace) =>
        Hash($"{exceptionType ?? ""}\n{Normalize(stackTrace)}");
}
