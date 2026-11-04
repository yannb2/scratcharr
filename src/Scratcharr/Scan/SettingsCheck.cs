using Scratcharr.Clients;
using Scratcharr.Options;

namespace Scratcharr.Scan;

public enum Severity
{
    Info,
    Warning
}

public sealed record Finding(Severity Severity, string Text);

/// <summary>A category of a download client and who looks after it.</summary>
public sealed record CategoryRow
{
    public required string Client { get; init; }
    public required string Category { get; init; }
    public string Folder { get; init; } = "";
    public string UsedBy { get; init; } = "";
    public string RemovedBy { get; init; } = "";
    public bool Nobody { get; init; }
    public int Items { get; init; }
}

public sealed record ArrClientRow(ArrApp App, ArrDownloadClient Client);

public sealed record SettingsReport
{
    public IReadOnlyList<CategoryRow> Categories { get; init; } = [];
    public IReadOnlyList<ArrClientRow> ArrClients { get; init; } = [];
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public string SeedingLimits { get; init; } = "";
    public string SabRetention { get; init; } = "";
}

/// <summary>What removes what, per client and category, with warnings about gaps.</summary>
public static class SettingsCheck
{
    public static SettingsReport Build(ScratcharrOptions options, Evidence evidence, IReadOnlyList<ItemReport> items)
    {
        var findings = new List<Finding>();
        var rows = new List<CategoryRow>();
        var arrClients = evidence.ArrStates.SelectMany(a => a.Clients.Select(c => new ArrClientRow(a.App, c))).ToList();
        string seeding = "";

        if (evidence.Qbittorrent.State is { } qbt)
        {
            var limits = qbt.Limits;
            seeding = (limits.Minutes, limits.Ratio) switch
            {
                (null, null) => "No global seeding limit.",
                ({ } m, null) => $"Stop seeding after {Classifier.Hours(m)}, then {ActionWords(limits.Action)}.",
                (null, { } r) => $"Stop seeding at ratio {r:0.##}, then {ActionWords(limits.Action)}.",
                ({ } m, { } r) => $"Stop seeding after {Classifier.Hours(m)} or ratio {r:0.##}, then {ActionWords(limits.Action)}."
            };

            if (limits.Action == ShareLimitAction.Remove)
            {
                findings.Add(new Finding(Severity.Warning,
                    "qBittorrent removes torrents at the seeding limit but keeps their files: every finished torrent leaves its files behind. " +
                    "Choose \"Remove torrent and its files\", or \"Stop torrent\" and let the arr apps remove them (Remove Completed)."));
            }

            if (limits.Minutes == null && limits.Ratio == null)
            {
                findings.Add(new Finding(Severity.Info, "qBittorrent has no seeding limit, so the arr apps never remove torrents (they wait for the limit)."));
            }

            var names = qbt.Categories.Keys.Append("").Distinct();
            foreach (var name in names)
            {
                var folder = name.Length == 0
                    ? qbt.DefaultSavePath
                    : qbt.Categories[name] is { Length: > 0 } path ? path : Path.Combine(qbt.DefaultSavePath, name);
                var client = Classifier.ArrClientFor(evidence, "QBittorrent", name);
                var count = qbt.Torrents.Count(t => t.Category == name);
                if (name.Length == 0 && count == 0)
                {
                    continue;
                }

                var (removedBy, nobody) = limits.Action switch
                {
                    ShareLimitAction.RemoveWithFiles => ("qBittorrent, with the files", false),
                    ShareLimitAction.Remove => ("qBittorrent, files left behind", true),
                    _ when client is { } c => c.Client.RemoveCompleted
                        ? ($"{c.App}, after the seeding limit", false)
                        : ($"Nobody ({c.App} has Remove Completed off)", true),
                    _ => ("Nobody", true)
                };
                rows.Add(new CategoryRow
                {
                    Client = "qBittorrent",
                    Category = name.Length == 0 ? "(none)" : name,
                    Folder = folder,
                    UsedBy = client is { } used ? $"{used.App} ({used.Client.Name})" : "",
                    RemovedBy = removedBy,
                    Nobody = nobody,
                    Items = count
                });

                if (name.Length == 0)
                {
                    findings.Add(new Finding(Severity.Warning,
                        $"{count} torrent(s) have no category, so they save to {folder} and nothing removes them. " +
                        "Give manual torrents the \"manual\" category (and set it as the default in Torrent Control)."));
                }
                else if (client == null && !name.Equals("manual", StringComparison.OrdinalIgnoreCase) && nobody)
                {
                    findings.Add(new Finding(Severity.Info, $"No arr app uses the qBittorrent category \"{name}\": its torrents stay until removed by hand."));
                }

                if (folder.Length > 0 && !folder.StartsWith(options.DownloadsRoot, StringComparison.Ordinal))
                {
                    findings.Add(new Finding(Severity.Warning, $"qBittorrent saves category \"{(name.Length == 0 ? "(none)" : name)}\" to {folder}, outside {options.DownloadsRoot}: Scratcharr can't see it."));
                }
            }

            if (!qbt.Categories.ContainsKey("manual"))
            {
                findings.Add(new Finding(Severity.Info, "qBittorrent has no \"manual\" category yet: add one so torrents added by hand land in torrents/manual."));
            }
        }

        string retention = "";
        if (evidence.Sabnzbd.State is { } sab)
        {
            retention = $"History {sab.HistoryRetention}.";
            foreach (var (name, dir) in sab.Categories.Where(c => c.Key != "*"))
            {
                var client = Classifier.ArrClientFor(evidence, "Sabnzbd", name);
                var folder = dir.Length == 0 ? Path.Combine(sab.CompleteFolder, name)
                    : dir.StartsWith('/') ? dir : Path.Combine(sab.CompleteFolder, dir);
                rows.Add(new CategoryRow
                {
                    Client = "SABnzbd",
                    Category = name,
                    Folder = folder,
                    UsedBy = client is { } used ? $"{used.App} ({used.Client.Name})" : "",
                    RemovedBy = client is { } c
                        ? c.Client.RemoveCompleted ? $"{c.App}, after the import" : $"Nobody ({c.App} has Remove Completed off)"
                        : "Nobody",
                    Nobody = client is not { Client.RemoveCompleted: true },
                    Items = sab.History.Count(j => j.Category == name)
                });

                if (client is { } off && !off.Client.RemoveCompleted)
                {
                    findings.Add(new Finding(Severity.Info,
                        $"{off.App} has Remove Completed off for SABnzbd: imported jobs stay in {folder} and show here as \"Imported, leftover\"."));
                }
            }

            if (!sab.Categories.ContainsKey("manual"))
            {
                findings.Add(new Finding(Severity.Info, "SABnzbd has no \"manual\" category yet: add one so NZBs added by hand land in usenet/complete/manual."));
            }
        }

        foreach (var row in arrClients.Where(r => r.Client.Enabled && !r.Client.RemoveFailed))
        {
            findings.Add(new Finding(Severity.Info, $"{row.App}: Remove Failed is off for {row.Client.Name}, so failed downloads stay."));
        }

        foreach (var folder in options.Folders.Where(f => !Directory.Exists(f.Path)))
        {
            findings.Add(new Finding(Severity.Info, $"{folder.Path} ({folder.Name}) does not exist yet."));
        }

        var nobodyBytes = items.Where(i => i.StaysForever).Sum(i => i.Item.Size);
        if (nobodyBytes > 0)
        {
            findings.Add(new Finding(Severity.Info, $"{Format.Size(nobodyBytes)} sits in items nothing will ever remove (manual items not counted)."));
        }

        return new SettingsReport
        {
            Categories = rows,
            ArrClients = arrClients,
            Findings = findings.OrderByDescending(f => f.Severity).ToList(),
            SeedingLimits = seeding,
            SabRetention = retention
        };
    }

    public static string ActionWords(ShareLimitAction action) => action switch
    {
        ShareLimitAction.Stop => "stop the torrent",
        ShareLimitAction.Remove => "remove the torrent (files stay)",
        ShareLimitAction.RemoveWithFiles => "remove the torrent and its files",
        ShareLimitAction.SuperSeeding => "super-seed",
        _ => action.ToString()
    };
}
