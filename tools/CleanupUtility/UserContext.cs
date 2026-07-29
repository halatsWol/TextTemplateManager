using Microsoft.Win32;
using System.Security.Principal;

namespace TtmCleanup;

/// <summary>The user whose install we're cleaning: their HKCU-equivalent registry root and their profile
/// paths. For the current user this is trivial; for /user:&lt;name&gt; it resolves the SID, finds the profile,
/// and (if they're not logged on) temporarily loads their NTUSER.DAT hive — which needs administrator.</summary>
internal sealed class UserContext : IDisposable
{
    public string UserName { get; private set; } = "";
    public string? Sid { get; private set; }
    public bool IsCurrentUser { get; private set; }
    public RegistryKey UserRoot { get; private set; } = null!;   // HKCU, or HKEY_USERS\<sid>

    public string LocalAppData { get; private set; } = "";
    public string RoamingAppData { get; private set; } = "";
    public string Desktop { get; private set; } = "";
    public string StartMenuPrograms { get; private set; } = "";

    private bool _hiveLoaded;
    private string? _hiveMount;

    public static UserContext Current()
    {
        var c = new UserContext
        {
            IsCurrentUser = true,
            UserName = Environment.UserName,
            UserRoot = Registry.CurrentUser,
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        c.StartMenuPrograms = Path.Combine(c.RoamingAppData, @"Microsoft\Windows\Start Menu\Programs");
        try { c.Sid = new WindowsPrincipal(WindowsIdentity.GetCurrent()).Identity is WindowsIdentity w ? w.User?.Value : null; } catch { }
        return c;
    }

    /// <summary>Resolve another local user. Returns null and sets <paramref name="error"/> on failure.</summary>
    public static UserContext? ForUser(string name, Logger log, out string error)
    {
        error = "";
        string sid;
        try { sid = ((SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier))).Value; }
        catch { error = $"could not resolve user '{name}' to a SID (is the name correct?)"; return null; }

        string? profile = ReadProfileDir(sid);
        if (string.IsNullOrEmpty(profile) || !Directory.Exists(profile))
        {
            error = $"could not find the profile folder for '{name}' (SID {sid}).";
            return null;
        }

        var c = new UserContext
        {
            UserName = name,
            Sid = sid,
            IsCurrentUser = false,
            LocalAppData = Path.Combine(profile, @"AppData\Local"),
            RoamingAppData = Path.Combine(profile, @"AppData\Roaming"),
            Desktop = Path.Combine(profile, "Desktop"),
        };
        c.StartMenuPrograms = Path.Combine(c.RoamingAppData, @"Microsoft\Windows\Start Menu\Programs");

        // Prefer the already-loaded hive (user is logged on); otherwise load NTUSER.DAT ourselves.
        try
        {
            var loaded = Registry.Users.OpenSubKey(sid, writable: true);
            if (loaded != null) { c.UserRoot = loaded; log.Verbose($"using live hive HKU\\{sid}"); return c; }
        }
        catch { }

        EnablePrivilege("SeBackupPrivilege");
        EnablePrivilege("SeRestorePrivilege");
        string mount = "TTMCleanup_" + sid;
        string datFile = Path.Combine(profile, "NTUSER.DAT");
        int rc = Native.RegLoadKeyW(Native.HKEY_USERS, mount, datFile);
        if (rc != 0)
        {
            error = $"could not load the registry hive for '{name}' (RegLoadKey error {rc}). Are you elevated and is the user logged off?";
            return null;
        }
        try
        {
            c.UserRoot = Registry.Users.OpenSubKey(mount, writable: true)
                         ?? throw new InvalidOperationException("mounted hive not openable");
            c._hiveLoaded = true; c._hiveMount = mount;
            log.Verbose($"loaded offline hive from {datFile} at HKU\\{mount}");
            return c;
        }
        catch (Exception ex)
        {
            Native.RegUnLoadKeyW(Native.HKEY_USERS, mount);
            error = "loaded the hive but could not open it: " + ex.Message;
            return null;
        }
    }

    private static string? ReadProfileDir(string sid)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            return k?.GetValue("ProfileImagePath") as string;   // REG_EXPAND_SZ is auto-expanded
        }
        catch { return null; }
    }

    private static void EnablePrivilege(string name)
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(),
                Native.TOKEN_ADJUST_PRIVILEGES | Native.TOKEN_QUERY, out nint tok)) return;
        try
        {
            if (!Native.LookupPrivilegeValueW(null, name, out long luid)) return;
            var tp = new Native.TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = Native.SE_PRIVILEGE_ENABLED };
            Native.AdjustTokenPrivileges(tok, false, ref tp, 0, nint.Zero, nint.Zero);
        }
        finally { Native.CloseHandle(tok); }
    }

    public void Dispose()
    {
        try { if (!IsCurrentUser) UserRoot?.Dispose(); } catch { }
        if (_hiveLoaded && _hiveMount != null)
        {
            // Handles must be gone before the hive will unload; force a collect and retry a few times.
            for (int i = 0; i < 5; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                if (Native.RegUnLoadKeyW(Native.HKEY_USERS, _hiveMount) == 0) { _hiveLoaded = false; break; }
                Thread.Sleep(150);
            }
        }
    }
}
