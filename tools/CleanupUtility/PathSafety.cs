namespace TtmCleanup;

/// <summary>Hard guard against ever deleting the wrong folder. A path (from the registry or computed) is
/// only accepted when it canonicalizes to something that structurally *must* be ours — i.e. it ends in the
/// expected `Marflow Software\<leaf>` and is nowhere near a drive root or a system directory.</summary>
internal static class PathSafety
{
    // A directory is deletable only if its last two segments equal this + the given leaf.
    private const string Vendor = "Marflow Software";

    public static bool ValidateDir(string? path, string requiredLeaf, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path)) return false;

        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return false; }
        full = full.TrimEnd('\\', '/');

        if (full.Length < 4) return false;                     // not a bare root
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || full.Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return false;                                      // equals its own drive/UNC root

        var segs = full.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 4) return false;                     // e.g. C:\Users\<u>\... — never shallow

        // Decisive check: last two segments must be exactly "Marflow Software\<leaf>".
        if (segs.Length < 2) return false;
        if (!segs[^2].Equals(Vendor, StringComparison.OrdinalIgnoreCase)) return false;
        if (!segs[^1].Equals(requiredLeaf, StringComparison.OrdinalIgnoreCase)) return false;

        if (IsProtected(full)) return false;
        return true;
    }

    // Never touch a path that equals, contains, or is contained by a critical location.
    private static bool IsProtected(string full)
    {
        foreach (var p in CriticalPaths())
        {
            if (string.IsNullOrEmpty(p)) continue;
            var c = p.TrimEnd('\\', '/');
            if (full.Equals(c, StringComparison.OrdinalIgnoreCase)) return true;
            if (c.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return true;  // full is an ancestor of a critical dir
        }
        return false;
    }

    private static IEnumerable<string> CriticalPaths()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetEnvironmentVariable("SystemDrive") + "\\";
    }
}
