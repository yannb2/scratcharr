using System.Text;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Clean;

/// <summary>The low space alert settings (Settings page). Off until turned on.</summary>
public sealed class SpaceAlertSettings
{
    public bool Enabled { get; set; }

    /// <summary>Alert when less than this share of the scratch pool is free.</summary>
    public int FreePercent { get; set; } = 10;
}

/// <summary>
/// An ntfy message when the scratch pool runs low, with the biggest items worth a look. It goes out once when the free
/// space drops below the threshold, then at most once a day while it stays below; climbing back above re-arms it.
/// </summary>
public static class SpaceAlert
{
    public static readonly TimeSpan Repeat = TimeSpan.FromHours(24);

    public const int Biggest = 5;

    public static double FreePercent(ScanResult scan) => scan.TotalBytes > 0 ? 100.0 * scan.FreeBytes / scan.TotalBytes : 100;

    /// <summary>The free share as text, like "4.2%".</summary>
    public static string Percent(ScanResult scan) => FreePercent(scan).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";

    public static bool Low(ScanResult scan, SpaceAlertSettings settings) =>
        scan is { Error: null, TotalBytes: > 0 } && FreePercent(scan) < settings.FreePercent;

    /// <summary>True when the alert should go out now.</summary>
    public static bool Due(ScanResult scan, SpaceAlertSettings settings, DateTime? lastSentUtc, DateTime nowUtc) =>
        settings.Enabled && Low(scan, settings) && (lastSentUtc is not { } last || nowUtc - last >= Repeat);

    /// <summary>The biggest items a person could remove: anything but manual or protected downloads, biggest first.</summary>
    public static IReadOnlyList<ItemReport> Candidates(ScanResult scan) =>
        scan.Items.Where(i => !i.IsManual && i.Verdict != Verdict.Protected && (i.Verdict.NeedsReview() || i.StaysForever))
            .OrderByDescending(i => i.Item.Size).Take(Biggest).ToList();

    public static string Text(ScanResult scan, SpaceAlertSettings settings, IReadOnlyList<RecycledItem> recycled, double recycleDays)
    {
        var text = new StringBuilder();
        text.AppendLine($"Only {Format.Size(scan.FreeBytes)} free of {Format.Size(scan.TotalBytes)} ({Percent(scan)}, alert below {settings.FreePercent}%).");
        var biggest = Candidates(scan);
        if (biggest.Count == 0)
        {
            text.AppendLine("Nothing is waiting for review: the space is in active downloads, manual or protected items.");
        }
        else
        {
            text.AppendLine("Biggest to review:");
            foreach (var item in biggest)
            {
                text.AppendLine($"- {item.Item.Name} ({Format.Size(item.Item.Size)}, {item.Verdict.Label().ToLowerInvariant()})");
            }
        }

        if (recycled.Count > 0)
        {
            text.Append($"The recycle folder holds {Format.Size(recycled.Sum(r => r.Size))}, deleted for good after {recycleDays.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} days.");
        }

        return text.ToString().Trim();
    }
}
