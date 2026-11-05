using System.IO.Hashing;

namespace Scratcharr.Usb;

/// <summary>The outcome of a copy: files and bytes copied, and whether every copy was read back and matched.</summary>
/// <param name="Resumed">Files kept from before a restart instead of being copied again.</param>
public sealed record CopyResult(int Files, long Bytes, bool Verified, int Resumed = 0);

/// <summary>
/// A file that was copied completely (written and flushed): its path inside the copy, the original's length and modified
/// time, and the xxHash3 of what was read. Kept in the job's list so a copy cut short by a restart carries on from the
/// next file.
/// </summary>
public sealed record CopiedFile(string Path, long Length, long SourceTicks, ulong Hash)
{
    public string Line => $"{Length}\t{SourceTicks}\t{Hash:x16}\t{Path}";

    public static CopiedFile? Parse(string line)
    {
        var parts = line.Split('\t', 4);
        return parts.Length == 4 && long.TryParse(parts[0], out var length) && long.TryParse(parts[1], out var ticks)
               && ulong.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var hash)
            ? new CopiedFile(parts[3], length, ticks, hash)
            : null;
    }
}

/// <summary>
/// Copies a file or folder from an external disk or share. Each file is hashed (xxHash3) while it is read; with verify on, the copy is
/// read back afterwards and its hash compared, so a flaky USB cable or disk shows up as a failed job, not a broken file.
/// The source is only read. Modified times are kept; symbolic links are skipped.
/// </summary>
public static class UsbCopy
{
    private const int BufferSize = 4 << 20;

    /// <param name="progress">Bytes done and the total (copy, then read-back when verifying).</param>
    /// <param name="copied">Files finished before a restart (empty for a new copy): kept when the original and the copy
    /// still have the same length and the original the same modified time; anything else is copied again.</param>
    /// <param name="fileDone">Called after each file is written and flushed, to remember it for a resume.</param>
    public static async Task<CopyResult> CopyAsync(string source, string target, bool verify, Action<long, long> progress,
        Action<string> log, CancellationToken ct, IReadOnlyCollection<CopiedFile>? copied = null, Action<CopiedFile>? fileDone = null)
    {
        var before = (copied ?? []).GroupBy(c => c.Path).ToDictionary(g => g.Key, g => g.Last());
        var resumed = 0;
        var isFile = File.Exists(source);
        var files = isFile
            ? [new FileInfo(source)]
            : CopyPlan.Files(source).OrderBy(f => f.FullName, StringComparer.Ordinal).ToList();
        var total = files.Sum(f => f.Length) * (verify ? 2 : 1);
        long done = 0;
        var hashes = new List<(string Target, ulong Hash, long Length)>();
        var buffer = new byte[BufferSize];

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = isFile ? file.Name : Path.GetRelativePath(source, file.FullName);
            var to = isFile ? target : Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (before.TryGetValue(relative, out var kept) && kept.Length == file.Length && kept.SourceTicks == file.LastWriteTimeUtc.Ticks
                && File.Exists(to) && new FileInfo(to).Length == file.Length)
            {
                hashes.Add((to, kept.Hash, file.Length));
                done += file.Length;
                progress(done, total);
                resumed++;
                continue;
            }

            if (File.Exists(to))
            {
                File.Delete(to); // cut short by a restart: this one starts again
            }

            log($"Copy {relative} ({Format.Size(file.Length)})");

            var hash = new XxHash3();
            await using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                output.SetLength(file.Length);
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.Append(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress(done, total);
                }

                // A file that changed size while it was read (still being written to the USB disk) is not a good copy.
                if (output.Position != file.Length)
                {
                    throw new IOException($"{file.Name} changed size while it was copied ({output.Position} of {file.Length} bytes).");
                }

                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }

            File.SetLastWriteTimeUtc(to, file.LastWriteTimeUtc);
            hashes.Add((to, hash.GetCurrentHashAsUInt64(), file.Length));
            fileDone?.Invoke(new CopiedFile(relative, file.Length, file.LastWriteTimeUtc.Ticks, hash.GetCurrentHashAsUInt64()));
        }

        if (resumed > 0)
        {
            log($"Resumed: {resumed} of {files.Count} files were already copied before the restart.");
        }

        if (!isFile)
        {
            Directory.CreateDirectory(target);
            KeepFolderTimes(source, target);
        }

        if (verify)
        {
            log("Reading the copy back to check it.");
            foreach (var (to, expected, length) in hashes)
            {
                ct.ThrowIfCancellationRequested();
                var hash = new XxHash3();
                await using var input = new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.Append(buffer.AsSpan(0, read));
                    done += read;
                    progress(done, total);
                }

                if (input.Length != length || hash.GetCurrentHashAsUInt64() != expected)
                {
                    throw new IOException($"The copy of {Path.GetFileName(to)} does not match the original (xxHash differs). Check the disk, cable or network share.");
                }
            }

            log($"All {hashes.Count} files match.");
        }

        return new CopyResult(files.Count, files.Sum(f => f.Length), verify, resumed);
    }

    /// <summary>Gives the copied folders the modified times of the originals, deepest first.</summary>
    private static void KeepFolderTimes(string source, string target)
    {
        var folders = Directory.GetDirectories(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .OrderByDescending(d => d.Length);
        foreach (var folder in folders.Append(source))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, folder));
            Directory.CreateDirectory(to); // empty folders too
            Directory.SetLastWriteTimeUtc(to, Directory.GetLastWriteTimeUtc(folder));
        }
    }
}
