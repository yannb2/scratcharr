using System.Net.Http.Headers;
using System.Text;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Clean;

/// <summary>The weekly ntfy message: what needs a look, what is new, what the recycle folder holds.</summary>
public static class Summary
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string Text(ScanResult scan, IReadOnlyList<RecycledItem> recycled, IReadOnlyList<ActionEntry> week, DateTime nowUtc)
    {
        var review = scan.Items.Where(i => i.Verdict.NeedsReview()).ToList();
        var fresh = review.Where(i => i.FirstSeen >= nowUtc.AddDays(-7)).ToList();
        var forever = scan.Items.Where(i => i.StaysForever && !i.Verdict.NeedsReview()).ToList();
        var deleted = week.Where(a => a.Ok && a.Action is "Delete" or "Purge").ToList();
        var recycledThisWeek = week.Count(a => a.Ok && a.Action == "Recycle");

        var text = new StringBuilder();
        if (review.Count == 0)
        {
            text.AppendLine("Nothing to review.");
        }
        else
        {
            var parts = review.GroupBy(i => i.Verdict).OrderByDescending(g => g.Sum(i => i.Item.Size))
                .Select(g => $"{g.Count()} {Noun(g.Key, g.Count())} ({Format.Size(g.Sum(i => i.Item.Size))})");
            text.AppendLine($"To review: {Count(review.Count)}, {Format.Size(review.Sum(i => i.Item.Size))}: {string.Join(", ", parts)}.");
            text.AppendLine(fresh.Count == 0
                ? "None of them is new this week."
                : $"New this week: {Count(fresh.Count)}, {Format.Size(fresh.Sum(i => i.Item.Size))} (biggest: {fresh.MaxBy(i => i.Item.Size)!.Item.Name}).");
        }

        if (forever.Count > 0)
        {
            text.AppendLine($"Nothing will remove {Count(forever.Count)} more ({Format.Size(forever.Sum(i => i.Item.Size))}).");
        }

        text.AppendLine(recycled.Count == 0
            ? "The recycle folder is empty."
            : $"Recycle folder: {Count(recycled.Count)}, {Format.Size(recycled.Sum(r => r.Size))}.");
        if (recycledThisWeek > 0 || deleted.Count > 0)
        {
            var parts = new List<string>();
            if (recycledThisWeek > 0)
            {
                parts.Add($"{recycledThisWeek} recycled");
            }

            if (deleted.Count > 0)
            {
                parts.Add($"{deleted.Count} deleted for good ({Format.Size(deleted.Sum(a => a.Size))})");
            }

            text.AppendLine($"This week: {string.Join(", ", parts)}.");
        }

        if (scan.TotalBytes > 0)
        {
            text.Append($"Free: {Format.Size(scan.FreeBytes)} of {Format.Size(scan.TotalBytes)}.");
        }

        return text.ToString().Trim();
    }

    /// <summary>The last time the summary was due: this week's day and hour, or last week's if that is still ahead.</summary>
    public static DateTime LastDue(NtfyOptions ntfy, DateTime nowLocal)
    {
        var due = nowLocal.Date.AddDays(-(((int)nowLocal.DayOfWeek - (int)ntfy.Day + 7) % 7)).AddHours(Math.Clamp(ntfy.Hour, 0, 23));
        return due > nowLocal ? due.AddDays(-7) : due;
    }

    public static async Task SendAsync(NtfyOptions ntfy, string text, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ntfy.Url) { Content = new StringContent(text, Encoding.UTF8) };
        request.Headers.Add("Title", "Scratcharr: weekly downloads summary");
        request.Headers.Add("Tags", "broom");
        if (!string.IsNullOrWhiteSpace(ntfy.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ntfy.Token.Trim());
        }

        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ntfy answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    private static string Noun(Verdict verdict, int n) => verdict switch
    {
        Verdict.Stray => n == 1 ? "stray" : "strays",
        Verdict.ImportedLeftover => n == 1 ? "imported leftover" : "imported leftovers",
        Verdict.Failed => "failed",
        Verdict.HalfFinished => "half-finished",
        _ => verdict.Label().ToLowerInvariant()
    };

    private static string Count(int n) => n == 1 ? "1 item" : $"{n} items";
}
