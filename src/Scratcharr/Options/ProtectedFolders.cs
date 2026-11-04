using Scratcharr.Scan;

namespace Scratcharr.Options;

/// <summary>The checks before a folder is added to the protected list on the Settings page.</summary>
public static class ProtectedFolders
{
    /// <summary>Null when the folder can be protected (path = its full path), else the reason it can't.</summary>
    public static string? Problem(ScratcharrOptions options, string? input, IReadOnlyList<string> current, out string? path)
    {
        path = null;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return "Type a folder under the downloads folder.";
        }

        if (text.Split('/').Any(part => part is ".." or "."))
        {
            return "Use a plain path, without . or ..";
        }

        var full = Paths.Clean(options.Absolute(text));
        if (!Paths.IsUnder(full, options.DownloadsRoot))
        {
            return $"Only folders under {options.DownloadsRoot} can be protected.";
        }

        if (current.Any(p => p == full || Paths.IsUnder(full, p)))
        {
            return $"{full} is protected already.";
        }

        path = full;
        return null;
    }
}
