using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FunctionLogMonitor.Services;

// The downstream locator parses text frames, not App Insights parsedStack JSON.
public static class StackFlattener
{
    /// <summary>Empty when nothing parses, so callers can fall back to raw text.</summary>
    public static string Flatten(string? detailsJson)
    {
        var frames = Parse(detailsJson);
        if (frames.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var f in frames.OrderBy(f => f.Level))
            sb.AppendLine(Format(f));
        return sb.ToString().TrimEnd();
    }

    // .g.cs frames are first-party by assembly and would win on level.
    public static Frame? TopFirstPartyFrame(string? detailsJson) =>
        Parse(detailsJson)
            .Where(f => f.Line > 0
                     && !string.IsNullOrEmpty(f.FileName)
                     && IsFirstParty(f.Assembly)
                     && !f.FileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                     && !f.FileName.StartsWith("/_/", StringComparison.Ordinal))
            .OrderBy(f => f.Level)
            .Cast<Frame?>()
            .FirstOrDefault();

    public static string Format(Frame f) =>
        f.Line > 0 && !string.IsNullOrEmpty(f.FileName)
            ? $"   at {f.Method}() in {f.FileName}:line {f.Line}"
            : $"   at {f.Method}()";

    public readonly record struct Frame(string Assembly, string Method, string FileName, int Line, int Level);

    private static readonly string[] FirstPartyPrefixes = { "Dan.", "Altinn.Dan", "Altinn.ApiClients" };

    private static bool IsFirstParty(string? assembly) =>
        assembly is not null
        && FirstPartyPrefixes.Any(p => assembly.StartsWith(p, StringComparison.Ordinal));

    // Recovers frames individually; payloads are routinely truncated mid-object.
    private static readonly Regex FrameRe = new(
        """\{"assembly":"(?<assembly>[^"]*)","method":"(?<method>[^"]*)","level":(?<level>\d+),"line":(?<line>\d+)(,"fileName":"(?<file>[^"]*)")?\}""",
        RegexOptions.Compiled);

    private static List<Frame> Parse(string? detailsJson)
    {
        var frames = new List<Frame>();
        if (string.IsNullOrWhiteSpace(detailsJson)) return frames;

        // Fenced when read back out of an issue body rather than off the API.
        var trimmed = detailsJson.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
            trimmed = trimmed.Trim('`').Trim();

        if (trimmed.Length > 0 && (trimmed[0] == '[' || trimmed[0] == '{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var detail in doc.RootElement.EnumerateArray()) Scan(detail, frames);
                else
                    Scan(doc.RootElement, frames);
                if (frames.Count > 0) return frames;
            }
            catch (JsonException)
            {
            }
        }

        foreach (Match m in FrameRe.Matches(trimmed))
        {
            frames.Add(new Frame(
                Assembly: m.Groups["assembly"].Value,
                Method: m.Groups["method"].Value,
                FileName: m.Groups["file"].Success ? m.Groups["file"].Value : "",
                Line: int.TryParse(m.Groups["line"].Value, out var l) ? l : 0,
                Level: int.TryParse(m.Groups["level"].Value, out var v) ? v : 0));
        }
        return frames;
    }

    private static void Scan(JsonElement detail, List<Frame> into)
    {
        if (detail.ValueKind != JsonValueKind.Object) return;
        if (!detail.TryGetProperty("parsedStack", out var stack)) return;
        if (stack.ValueKind != JsonValueKind.Array) return;

        foreach (var fr in stack.EnumerateArray())
        {
            if (fr.ValueKind != JsonValueKind.Object) continue;
            into.Add(new Frame(
                Assembly: Str(fr, "assembly"),
                Method: Str(fr, "method"),
                FileName: Str(fr, "fileName"),
                Line: Num(fr, "line"),
                Level: Num(fr, "level")));
        }
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Num(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : 0;
}
