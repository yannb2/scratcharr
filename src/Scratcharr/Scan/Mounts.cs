namespace Scratcharr.Scan;

/// <summary>The mount points the container sees, from /proc/self/mountinfo.</summary>
public static class Mounts
{
    /// <summary>The mount a path sits on: the longest mount point that holds it. Null when it can't be read.</summary>
    public static string? Of(string path)
    {
        try
        {
            return File.ReadLines("/proc/self/mountinfo")
                .Select(l => l.Split(' '))
                .Where(f => f.Length > 4)
                .Select(f => f[4].Replace("\\040", " "))
                .Where(m => m == "/" || m == path || Paths.IsUnder(path, m))
                .MaxBy(m => m.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
