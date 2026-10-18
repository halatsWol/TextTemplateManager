using System;
using System.Diagnostics;
using System.IO;

namespace TextTemplateManager.Services.System;

/// <summary>Removes this installation with its own (Inno Setup) uninstaller. The data folder stays.</summary>
public static class SelfUninstall
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{9C4E7B2A-1F53-4A8D-B6E0-3D7C2F9A15E4}_is1";

    /// <summary>Starts the uninstaller silently. False when this copy wasn't installed by the installer (a dev
    /// or portable build), so a different, installed copy is never removed by mistake.</summary>
    public static bool Start()
    {
        string? uninstaller = UninstallerFor(ReadUninstallString(), AppContext.BaseDirectory);
        if (uninstaller == null) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>The uninstaller from the uninstall entry's command line, but only if it lies in
    /// <paramref name="appDirectory"/>, the folder this copy runs from.</summary>
    internal static string? UninstallerFor(string? uninstallString, string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(uninstallString)) return null;
        string command = uninstallString.Trim();
        string exe;
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end < 0) return null;
            exe = command[1..end];
        }
        else
        {
            // Unquoted (Inno quotes it, but the path may contain spaces): the path runs up to ".exe".
            int end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = end >= 0 ? command[..(end + 4)] : command;
        }

        try
        {
            string exeDir = Path.GetFullPath(Path.GetDirectoryName(exe) ?? "").TrimEnd('\\');
            string appDir = Path.GetFullPath(appDirectory).TrimEnd('\\');
            return string.Equals(exeDir, appDir, StringComparison.OrdinalIgnoreCase) ? exe : null;
        }
        catch { return null; }
    }

    private static string? ReadUninstallString()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("UninstallString") as string;
        }
        catch { return null; }
    }
}
