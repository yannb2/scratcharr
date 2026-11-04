using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Scratcharr.Unpack;

/// <summary>One file or folder inside an archive.</summary>
public sealed record ArchiveEntry(string Path, long Size, bool IsFolder);

/// <summary>What an archive holds, from <c>7zz l -slt</c>.</summary>
public sealed record ArchiveListing
{
    public IReadOnlyList<ArchiveEntry> Entries { get; init; } = [];

    /// <summary>Set when 7-Zip could not read it (wrong password, missing part, damaged).</summary>
    public string? Error { get; init; }

    public long UnpackedSize => Entries.Where(e => !e.IsFolder).Sum(e => e.Size);

    public int Files => Entries.Count(e => !e.IsFolder);

    public int Isos => Entries.Count(e => !e.IsFolder && e.Path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));

    public bool HasBdmv => Entries.Any(e => e.Path.Split('/', '\\').Any(p => p.Equals("BDMV", StringComparison.OrdinalIgnoreCase)));

    public int Videos => Entries.Count(e => !e.IsFolder && Scan.DiskScanner.IsVideo(e.Path));

    /// <summary>"1 ISO", "a BDMV folder", "3 videos", "12 files".</summary>
    public string Summary
    {
        get
        {
            if (Error != null)
            {
                return Error;
            }

            var parts = new List<string>();
            if (Isos > 0)
            {
                parts.Add(Isos == 1 ? "1 ISO" : $"{Isos} ISOs");
            }

            if (HasBdmv)
            {
                parts.Add("a BDMV folder");
            }

            if (Videos > 0)
            {
                parts.Add(Videos == 1 ? "1 video" : $"{Videos} videos");
            }

            parts.Add(Files == 1 ? "1 file in all" : $"{Files} files in all");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Runs 7-Zip (<c>7zz</c>, pinned in the image). Reading only: it lists archives and extracts them into a
/// folder Scratcharr chooses; it never deletes or changes an archive.
/// </summary>
public static partial class SevenZip
{
    /// <summary>
    /// Always passed: an encrypted archive then fails with "Wrong password" at once instead of waiting
    /// for a password on the console. Archives without a password ignore it.
    /// </summary>
    private const string NoPassword = "-p-";

    public static async Task<ArchiveListing> ListAsync(string tool, string start, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        RunResult result;
        try
        {
            result = await RunAsync(tool, ["l", "-slt", NoPassword, "--", start], line => output.AppendLine(line), TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ArchiveListing { Error = "7-Zip took more than 2 minutes to list it." };
        }

        if (result.ExitCode != 0)
        {
            return new ArchiveListing { Error = Reason(result.ExitCode, result.Error + "\n" + output) };
        }

        return new ArchiveListing { Entries = ParseListing(output.ToString()) };
    }

    /// <summary>Extracts into <paramref name="target"/> (created by 7-Zip), calling <paramref name="progress"/> with 0-100.</summary>
    public static Task<RunResult> ExtractAsync(string tool, string start, string target, Action<int> progress, Action<string> log,
        CancellationToken cancellationToken) =>
        RunAsync(tool, ["x", "-y", "-bb1", "-bsp1", "-bso1", "-bse2", NoPassword, "-o" + target, "--", start], line =>
        {
            if (Percent(line) is { } percent)
            {
                progress(percent);
            }
            else if (line.Trim().Length > 0)
            {
                log(line.Trim());
            }
        }, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>
    /// The entries after the "----------" line of <c>7zz l -slt</c> (the block before it is the archive itself).
    /// Each entry starts with its "Path = " line; blank lines are not relied on.
    /// </summary>
    internal static List<ArchiveEntry> ParseListing(string text)
    {
        var entries = new List<ArchiveEntry>();
        Dictionary<string, string>? fields = null;
        var inEntries = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (!inEntries)
            {
                inEntries = line == "----------";
                continue;
            }

            var pair = line.Split(" = ", 2);
            var key = pair[0].Trim();
            var value = pair.Length == 2 ? pair[1].Trim() : "";
            if (pair.Length < 2 && !line.EndsWith(" =", StringComparison.Ordinal))
            {
                continue;
            }

            if (key == "Path")
            {
                Add(fields);
                fields = new Dictionary<string, string>();
            }

            fields?.TryAdd(key.TrimEnd(' ', '='), value);
        }

        Add(fields);
        return entries;

        void Add(Dictionary<string, string>? entry)
        {
            if (entry == null || entry.GetValueOrDefault("Path", "").Length == 0)
            {
                return;
            }

            var isFolder = entry.GetValueOrDefault("Folder") == "+" || entry.GetValueOrDefault("Attributes", "").StartsWith('D');
            long.TryParse(entry.GetValueOrDefault("Size"), out var size);
            entries.Add(new ArchiveEntry(entry["Path"].Replace('\\', '/'), size, isFolder));
        }
    }

    /// <summary>The percentage in a 7-Zip progress line ("  42% 3 - movie.iso"), or null.</summary>
    internal static int? Percent(string line)
    {
        var match = PercentPattern().Match(line);
        return match.Success ? Math.Clamp(int.Parse(match.Groups[1].Value), 0, 100) : null;
    }

    /// <summary>A short reason from 7-Zip's exit code and its error lines.</summary>
    internal static string Reason(int exitCode, string text)
    {
        if (exitCode == NotStartedCode)
        {
            return "7-Zip is not installed (7zz not found).";
        }

        if (text.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) || text.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
        {
            return "The archive has a password.";
        }

        if (text.Contains("Missing volume", StringComparison.OrdinalIgnoreCase) || text.Contains("Unexpected end of archive", StringComparison.OrdinalIgnoreCase))
        {
            return "A part of the archive is missing or unfinished.";
        }

        if (text.Contains("open the file as archive", StringComparison.OrdinalIgnoreCase))
        {
            return "7-Zip can't open it as an archive: damaged, unfinished, or not an archive.";
        }

        if (text.Contains("No space left", StringComparison.OrdinalIgnoreCase))
        {
            return "The disk is full.";
        }

        if (text.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase))
        {
            return "/downloads is mounted read-only for Scratcharr.";
        }

        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) || l.Contains("Can not", StringComparison.OrdinalIgnoreCase));
        return line ?? (exitCode == 2 ? "7-Zip reported a fatal error." : $"7-Zip stopped with exit code {exitCode}.");
    }

    public const int NotStartedCode = -1;

    public sealed record RunResult(int ExitCode, string Error);

    /// <summary>
    /// Runs the tool with stdin closed, handing every output line (split on new lines, carriage returns
    /// and the backspaces 7-Zip uses to redraw its percentage) to <paramref name="onLine"/>.
    /// </summary>
    private static async Task<RunResult> RunAsync(string tool, IEnumerable<string> arguments, Action<string> onLine, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            return new RunResult(NotStartedCode, e.Message);
        }

        process.StandardInput.Close();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            stop.CancelAfter(timeout);
        }

        var error = process.StandardError.ReadToEndAsync(stop.Token);
        try
        {
            var buffer = new char[4096];
            var line = new StringBuilder();
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer, stop.Token)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c is '\n' or '\r' or '\b')
                    {
                        if (line.Length > 0)
                        {
                            onLine(line.ToString());
                            line.Clear();
                        }
                    }
                    else
                    {
                        line.Append(c);
                    }
                }
            }

            if (line.Length > 0)
            {
                onLine(line.ToString());
            }

            await process.WaitForExitAsync(stop.Token);
            return new RunResult(process.ExitCode, await error);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }
    }

    [GeneratedRegex(@"^\s*(\d{1,3})%")]
    private static partial Regex PercentPattern();
}
