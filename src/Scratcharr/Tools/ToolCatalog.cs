using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scratcharr.Options;

namespace Scratcharr.Tools;

/// <summary>Where a tool's newest version is read.</summary>
public enum UpstreamKind
{
    None,

    /// <summary>A web page; the highest version the regex finds.</summary>
    Page,

    /// <summary>The latest release of a GitHub repository (owner/name).</summary>
    GitHubRelease
}

/// <summary>A tool in the image: how to ask its version, which version the image pins, where to look for a newer one.</summary>
public sealed record ToolDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Purpose { get; init; }
    public required Func<ToolPaths, string> Command { get; init; }
    public IReadOnlyList<string> VersionArguments { get; init; } = [];
    public required Regex VersionPattern { get; init; }
    public UpstreamKind Upstream { get; init; }

    /// <summary>The page URL(s) or the GitHub owner/name.</summary>
    public IReadOnlyList<string> UpstreamSources { get; init; } = [];

    public Regex? UpstreamPattern { get; init; }
    public string? ReleaseNotes { get; init; }

    /// <summary>The version the Dockerfile installed (SCRATCHARR_PIN_MAKEMKV...).</summary>
    public string? Pinned => Environment.GetEnvironmentVariable($"SCRATCHARR_PIN_{Id.ToUpperInvariant()}");
}

public static partial class ToolCatalog
{
    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new()
        {
            Id = "7zip", Name = "7-Zip", Purpose = "Unpacks .rar sets (RAR4 and RAR5, multi-part), .7z and .zip: archived ISOs for Ripparr (v0.2).",
            Command = t => t.SevenZip,
            VersionPattern = SevenZipVersion(),
            Upstream = UpstreamKind.GitHubRelease,
            UpstreamSources = ["ip7z/7zip"],
            ReleaseNotes = "https://www.7-zip.org/history.txt"
        }
    ];

    /// <summary>The installed version in a tool's own output.</summary>
    public static string? InstalledVersion(ToolDefinition tool, string output) =>
        tool.VersionPattern.Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The highest version the upstream pattern finds on a page.</summary>
    public static string? HighestOnPage(Regex pattern, string page, Func<string, bool>? accept = null)
    {
        return pattern.Matches(page)
            .Select(m => m.Groups[1].Value)
            .Where(v => accept?.Invoke(v) ?? true)
            .OrderByDescending(v => v, VersionComparer.Instance)
            .FirstOrDefault();
    }

    /// <summary>
    /// The highest plain version tag (1.2.3 or v1.2.3) of a GitHub releases answer, a list or a single
    /// release. Other tags are skipped: dovi_tool's repository also has libdovi-3.x releases.
    /// </summary>
    public static string? GitHubTag(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var releases = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : [document.RootElement];
            return releases
                .Where(r => r.ValueKind == JsonValueKind.Object
                            && !(r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True)
                            && !(r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True))
                .Select(r => r.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "")
                .Where(t => PlainVersionTag().IsMatch(t))
                .Select(t => t.TrimStart('v', 'V'))
                .OrderByDescending(v => v, VersionComparer.Instance)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static int Major(string version) =>
        int.TryParse(version.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : 0;

    [GeneratedRegex(@"^[vV]?\d+(?:\.\d+)+$")]
    private static partial Regex PlainVersionTag();

    [GeneratedRegex(@"7-Zip (?:\([az]\) )?(?:\[\d+\] )?(\d+\.\d+)")]
    private static partial Regex SevenZipVersion();
}

/// <summary>Compares "1.18.4" and "1.18.10" by their numbers, not as text.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = Parts(x);
        var b = Parts(y);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    private static int[] Parts(string? version) =>
        (version ?? "").Split('.', '-', '+')
        .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
        .ToArray();
}
