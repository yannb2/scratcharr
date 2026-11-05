using System.Text.Json;
using Arrkit.Library;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Jobs;

/// <summary>
/// "Copy as extras": videos from a download or an external disk into a film's or series' folder, each in its Jellyfin
/// folder (featurettes/, trailers/...). The plan and the copy are arrkit's (Arrkit.Library), shared with Ripparr.
/// </summary>
public static class ExtrasJob
{
    /// <summary>The settings key holding a job's list of extras.</summary>
    public static string Key(long id) => $"job.{id}.extras";

    public static string Save(IEnumerable<ExtraFile> files) => JsonSerializer.Serialize(files.ToList(), Store.Json);

    public static List<ExtraFile> Load(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ExtraFile>>(json, Store.Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A file or folder extras can come from: in the downloads or on an external disk.</summary>
    public static bool AllowedSource(ScratcharrOptions options, string path) =>
        Paths.IsUnder(path, options.DownloadsRoot) || Paths.IsUnder(path, options.UsbRoot);

    /// <summary>Why these extras can't be queued, or null.</summary>
    public static string? Problem(ScratcharrOptions options, string source, string titleFolder, IReadOnlyList<ExtraStep> steps, IEnumerable<Job> openJobs)
    {
        if (!options.AllowChanges)
        {
            return "Changes are off: set Scratcharr__AllowChanges=true and mount /movies and /tv read-write to copy extras.";
        }

        if (!AllowedSource(options, source) || steps.Any(s => !AllowedSource(options, s.File.Source)))
        {
            return $"Extras are copied only from {options.DownloadsRoot} or {options.UsbRoot}.";
        }

        if (steps.Count == 0)
        {
            return "Tick at least one video.";
        }

        if (steps.FirstOrDefault(s => s.State == ExtraStepState.Problem) is { } bad)
        {
            return $"{System.IO.Path.GetFileName(bad.File.Source)}: {bad.Problem}";
        }

        if (!steps.Any(s => s.ToCopy))
        {
            return "They are all in the library already.";
        }

        return openJobs.Any(j => j is { Open: true, Kind: JobKind.Extras } && j.Target == titleFolder && j.Source == source)
            ? "These extras are already queued."
            : null;
    }
}
