using Microsoft.Win32;
using System;

namespace TextTemplateManager.Services.System;

/// <summary>Autostart on Windows login, driven entirely by the registry (nothing is stored in settings).
/// Two keys, mirroring how Task Manager / Settings › Startup work:
///   • HKCU\...\CurrentVersion\Run            — WHAT to launch (value data = command line).
///   • HKCU\...\Explorer\StartupApproved\Run  — a per-app enabled/disabled flag Windows honors at login.
/// A Run value with no StartupApproved record counts as enabled; a record whose first byte's low bit is set
/// (0x03) means disabled. We write 0x02 on enable and 0x03 on disable so our toggle and the Windows UI always
/// agree — and, because the record survives an uninstall (only the Run value is removed), the on/off choice
/// is remembered across a reinstall.</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    // Must match the installer's value name ({#MyAppName}).
    private const string ValueName = "TextTemplateManager";

    // Appended to the Run command line so the login launch starts hidden in the tray (see App.OnLaunched).
    public const string HiddenFlag = "--hidden";

    private static string ExePath =>
        Environment.ProcessPath
        ?? global::System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? string.Empty;

    /// <summary>True when a Run value exists and it isn't disabled in StartupApproved.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (run?.GetValue(ValueName) == null) return false;
            return !IsApprovedDisabled();
        }
        catch { return false; }
    }

    /// <summary>True when the Run command line carries the hidden-start flag.</summary>
    public static bool IsHiddenEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return run?.GetValue(ValueName) is string data
                   && data.IndexOf(HiddenFlag, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    /// <summary>Enable or disable autostart. Enable (re)writes the Run command line (with or without the hidden
    /// flag) and marks StartupApproved enabled. Disable leaves the Run value untouched — preserving the hidden
    /// flag — and only writes the disabled marker, exactly like Task Manager. Best-effort.</summary>
    public static void SetEnabled(bool enabled, bool hidden)
    {
        try
        {
            if (enabled)
            {
                string path = ExePath;
                if (string.IsNullOrEmpty(path)) return;
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKey);
                run?.SetValue(ValueName, hidden ? $"\"{path}\" {HiddenFlag}" : $"\"{path}\"");
            }
            WriteApproved(disabled: !enabled);
        }
        catch { /* best effort — user may have a locked-down policy */ }
    }

    // True only when a StartupApproved record exists and its first byte marks it disabled (low bit set).
    private static bool IsApprovedDisabled()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: false);
        return approved?.GetValue(ValueName) is byte[] b && b.Length >= 1 && (b[0] & 1) == 1;
    }

    // 12-byte record: byte 0 = 0x02 enabled / 0x03 disabled; bytes 4..11 = FILETIME of the disable (0 if enabled).
    private static void WriteApproved(bool disabled)
    {
        var blob = new byte[12];
        blob[0] = (byte)(disabled ? 0x03 : 0x02);
        if (disabled)
            BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(blob, 4);   // little-endian on Windows

        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(ApprovedKey);
        approved?.SetValue(ValueName, blob, RegistryValueKind.Binary);
    }
}
