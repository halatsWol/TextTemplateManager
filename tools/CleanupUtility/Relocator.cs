using System.Diagnostics;

namespace TtmCleanup;

/// <summary>When the tool runs from inside the folder it's about to delete (the copy bundled in the install
/// dir), it copies itself to %TEMP% and relaunches from there, so the running image isn't in the folder
/// being removed. The relocated child waits for the original to exit, then does the work.</summary>
internal static class Relocator
{
    private const string Marker = "--relocated";

    /// <summary>If args carry the relocated marker, strip it and return the parent PID to wait for.</summary>
    public static int? ExtractParentPid(ref string[] args)
    {
        int? pid = null;
        var kept = new List<string>(args.Length);
        foreach (var a in args)
        {
            string s = a;
            int sep = s.IndexOfAny(new[] { ':', '=' });
            string name = (sep >= 0 ? s[..sep] : s).TrimStart('/', '-').ToLowerInvariant();
            if (name == "relocated")
            {
                if (sep >= 0 && int.TryParse(s[(sep + 1)..], out int p)) pid = p;
                continue;   // drop it
            }
            kept.Add(a);
        }
        args = kept.ToArray();
        return pid;
    }

    // The current-user install parent — the only place a bundled copy of us would live.
    public static bool RunningInsideCurrentInstall()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            string parent = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Marflow Software");
            return Path.GetFullPath(exe).StartsWith(Path.GetFullPath(parent) + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Copy self to %TEMP% and relaunch with the same args + marker. Returns false if it couldn't.</summary>
    public static bool RelaunchFromTemp(string[] args)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            string temp = Path.Combine(Path.GetTempPath(), $"ttm-cleanup-{Guid.NewGuid():N}.exe");
            File.Copy(exe, temp, overwrite: true);

            var psi = new ProcessStartInfo(temp) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add($"{Marker}:{Environment.ProcessId}");
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    public static void WaitForParent(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(15000); }
        catch { /* already gone */ }
    }

    /// <summary>Best-effort: schedule this temp copy to be deleted shortly after we exit.</summary>
    public static void ScheduleSelfDelete()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & del /f /q \"{exe}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
    }
}
