using Microsoft.Win32;
using System;

namespace TextTemplateManager.Services.System;

/// <summary>The "run at Windows login" entry under HKCU\...\CurrentVersion\Run — the same
/// key/value the installer's autostart task writes, so switch and installer stay in sync.</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Must match the installer's [Registry] ValueName ({#MyAppName}).
    private const string ValueName = "TextTemplateManager";

    private static string ExePath =>
        Environment.ProcessPath
        ?? global::System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? string.Empty;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    /// <summary>Add (pointing at the current exe) or remove the autostart entry. Best-effort.</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;

            if (enabled)
            {
                string path = ExePath;
                if (!string.IsNullOrEmpty(path))
                    key.SetValue(ValueName, $"\"{path}\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best effort — user may have locked-down policy */ }
    }
}
