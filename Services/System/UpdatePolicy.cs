using Microsoft.Win32;

namespace TextTemplateManager.Services.System;

public enum UpdatePolicyLevel
{
    AllowAll = 0,   // updates and beta allowed
    NoBeta = 1,     // stable updates allowed, beta blocked
    NoUpdate = 2,   // no update checks at all
}

/// <summary>
/// Read-only enterprise policy for auto-update, from <c>Software\MarflowSoftware\TextTemplateManager\allowUpdate</c>
/// (a DWORD, or numeric string). The app never writes this key, so an administrator can lock it
/// down via HKLM. HKLM takes precedence over HKCU; a missing value means <see cref="UpdatePolicyLevel.AllowAll"/>.
/// </summary>
public static class UpdatePolicy
{
    private const string PolicyKey = @"Software\MarflowSoftware\TextTemplateManager";
    private const string ValueName = "allowUpdate";

    public static UpdatePolicyLevel Current
    {
        get
        {
            // HKLM wins if present; otherwise HKCU; otherwise unset (allow all).
            int level = ReadValue(Registry.LocalMachine) ?? ReadValue(Registry.CurrentUser) ?? 0;
            return level >= 2 ? UpdatePolicyLevel.NoUpdate
                 : level == 1 ? UpdatePolicyLevel.NoBeta
                 : UpdatePolicyLevel.AllowAll;
        }
    }

    public static bool UpdatesAllowed => Current != UpdatePolicyLevel.NoUpdate;
    public static bool BetaAllowed => Current == UpdatePolicyLevel.AllowAll;

    private static int? ReadValue(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(PolicyKey, writable: false);
            return key?.GetValue(ValueName) switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s.Trim(), out int p) => p,
                _ => null,
            };
        }
        catch { return null; }   // locked-down / inaccessible policy hive
    }
}
