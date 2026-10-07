using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FunctionLogMonitor.Services;

// The downstream locator parses text frames, not App Insights parsedStack JSON.
public static class StackFlattener
{
    private const int MaxMessageLength = 300;

    /// <summary>Empty when nothing parses, so callers can fall back to raw text.</summary>
    public static string Flatten(string? detailsJson)
    {
        var chain = Parse(detailsJson);
        if (chain.Sum(e => e.Frames.Count) == 0) return "";

        var sb = new StringBuilder();
        if (chain.Count == 1)
        {
            AppendFrames(sb, chain[0]);
            return sb.ToString().TrimEnd();
        }

        // .NET's own Exception.ToString shape: headers outer to inner, frames inner to outer.
        for (var i = 0; i < chain.Count; i++)
            sb.AppendLine((i == 0 ? "" : " ---> ") + Header(chain[i]));
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            AppendFrames(sb, chain[i]);
            if (i > 0) sb.AppendLine("   --- End of inner exception stack trace ---");
        }
        return sb.ToString().TrimEnd();
    }

    // Innermost first: that is where the failing call is, not where it was rewrapped.
    // .g.cs frames are first-party by assembly and would win on level.
    public static Frame? TopFirstPartyFrame(string? detailsJson) =>
        Parse(detailsJson)
            .AsEnumerable()
            .Reverse()
            .Select(e => e.Frames
                .Where(f => f.Line > 0
                         && !string.IsNullOrEmpty(f.FileName)
                         && IsFirstParty(f.Assembly)
                         && !f.FileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                         && !f.FileName.StartsWith("/_/", StringComparison.Ordinal))
                .OrderBy(f => f.Level)
                .Cast<Frame?>()
                .FirstOrDefault())
            .FirstOrDefault(f => f is not null);

    // Innermost exception first, matching TopFirstPartyFrame.
    public static IReadOnlyList<Frame> FirstPartyFrames(string? detailsJson) =>
        Parse(detailsJson)
            .AsEnumerable()
            .Reverse()
            .SelectMany(e => e.Frames
                .Where(f => IsFirstParty(f.Assembly)
                         && !f.FileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Level))
            .ToList();

    public static string Format(Frame f) =>
        f.Line > 0 && !string.IsNullOrEmpty(f.FileName)
            ? $"   at {f.Method}() in {f.FileName}:line {f.Line}"
            : $"   at {f.Method}()";

    public readonly record struct Frame(string Assembly, string Method, string FileName, int Line, int Level);

    private sealed record Exc(string Type, string Message, List<Frame> Frames);

    private static void AppendFrames(StringBuilder sb, Exc e)
    {
        foreach (var f in e.Frames.OrderBy(f => f.Level))
            sb.AppendLine(Format(f));
    }

    private static string Header(Exc e)
    {
        var message = Regex.Replace(e.Message, @"\s+", " ").Trim();
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength] + "...";
        var type = e.Type.Length > 0 ? e.Type : "UnknownException";
        return message.Length > 0 ? $"{type}: {message}" : type;
    }

    private static readonly string[] FirstPartyPrefixes = { "Dan.", "Altinn.Dan", "Altinn.ApiClients" };

    private static bool IsFirstParty(string? assembly) =>
        assembly is not null
        && FirstPartyPrefixes.Any(p => assembly.StartsWith(p, StringComparison.Ordinal));

    // Recovers frames individually; payloads are routinely truncated mid-object.
    private static readonly Regex FrameRe = new(
        """\{"assembly":"(?<assembly>[^"]*)","method":"(?<method>[^"]*)","level":(?<level>\d+),"line":(?<line>\d+)(,"fileName":"(?<file>[^"]*)")?\}""",
        RegexOptions.Compiled);

    // Outer exception first, as App Insights emits them.
    private static List<Exc> Parse(string? detailsJson)
    {
        var chain = new List<Exc>();
        if (string.IsNullOrWhiteSpace(detailsJson)) return chain;

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
                    foreach (var detail in doc.RootElement.EnumerateArray()) Scan(detail, chain);
                else
                    Scan(doc.RootElement, chain);
                if (chain.Any(e => e.Frames.Count > 0)) return chain;
                chain.Clear();
            }
            catch (JsonException)
            {
            }
        }

        var frames = new List<Frame>();
        foreach (Match m in FrameRe.Matches(trimmed))
        {
            frames.Add(new Frame(
                Assembly: m.Groups["assembly"].Value,
                Method: m.Groups["method"].Value,
                FileName: m.Groups["file"].Success ? m.Groups["file"].Value : "",
                Line: int.TryParse(m.Groups["line"].Value, out var l) ? l : 0,
                Level: int.TryParse(m.Groups["level"].Value, out var v) ? v : 0));
        }
        // Truncated payloads cannot be split per exception.
        if (frames.Count > 0) chain.Add(new Exc("", "", frames));
        return chain;
    }

    private static void Scan(JsonElement detail, List<Exc> into)
    {
        if (detail.ValueKind != JsonValueKind.Object) return;
        var frames = new List<Frame>();
        into.Add(new Exc(Str(detail, "type"), Str(detail, "message"), frames));
        if (!detail.TryGetProperty("parsedStack", out var stack)) return;
        if (stack.ValueKind != JsonValueKind.Array) return;

        foreach (var fr in stack.EnumerateArray())
        {
            if (fr.ValueKind != JsonValueKind.Object) continue;
            frames.Add(new Frame(
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
