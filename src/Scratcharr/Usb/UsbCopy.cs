using System.IO.Hashing;

namespace Scratcharr.Usb;

/// <summary>The outcome of a copy: files and bytes copied, and whether every copy was read back and matched.</summary>
public sealed record CopyResult(int Files, long Bytes, bool Verified);

/// <summary>
/// Copies a file or folder from a USB disk. Each file is hashed (xxHash3) while it is read; with verify on, the copy is
/// read back afterwards and its hash compared, so a flaky USB cable or disk shows up as a failed job, not a broken file.
/// The source is only read. Modified times are kept; symbolic links are skipped.
/// </summary>
public static class UsbCopy
{
    private const int BufferSize = 4 << 20;

    /// <param name="progress">Bytes done and the total (copy, then read-back when verifying).</param>
    public static async Task<CopyResult> CopyAsync(string source, string target, bool verify, Action<long, long> progress,
        Action<string> log, CancellationToken ct)
    {
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
            var to = isFile ? target : Path.Combine(target, Path.GetRelativePath(source, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            log($"Copy {(isFile ? file.Name : Path.GetRelativePath(source, file.FullName))} ({Format.Size(file.Length)})");

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

        return new CopyResult(files.Count, files.Sum(f => f.Length), verify);
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
