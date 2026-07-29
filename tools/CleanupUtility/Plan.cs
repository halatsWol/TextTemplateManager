namespace TtmCleanup;

/// <summary>The concrete set of registry keys and filesystem paths that constitute an installation,
/// computed for a specific user. Registry paths are relative to the user's root (HKCU or HKU\&lt;sid&gt;).</summary>
internal sealed class Plan
{
    // ---- Registry (relative to the user root) ----
    private const string Guid = "9C4E7B2A-1F53-4A8D-B6E0-3D7C2F9A15E4";
    public const string UninstallKeyClean = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{{{Guid}}}_is1";
    // The historical typo: an extra closing brace, so the key is literally "{GUID}}_is1".
    public const string UninstallKeyTypo = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{{{Guid}}}}}_is1";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ClassesKey = @"Software\Classes";
    public const string ValueName = "TextTemplateManager";
    public const string ProgId = "TextTemplateManager.ttmdata";
    public const string Ext = ".ttmdata";

    public static readonly string[] UninstallKeys = { UninstallKeyClean, UninstallKeyTypo };

    // ---- Validation leaves (last two path segments must match these) ----
    public const string InstallLeaf = "TextTemplateManager";
    public const string DataLeaf = "TextTemplateManager";
    public const string LegacyDataLeaf = "TextTemplatesManager";

    // ---- Filesystem (computed per user) ----
    public string InstallDefault { get; }
    public string InstallParent { get; }
    public string DataDir { get; }
    public string LegacyDataDir { get; }
    public string StartMenuLnk { get; }
    public string DesktopLnk { get; }

    public string SettingsFile => Path.Combine(DataDir, "settings.ttmsettings");
    public string SyncFile => Path.Combine(DataDir, "sync.ttmsettings");
    public string TemplatesFile => Path.Combine(DataDir, "data.ttmdata");

    public Plan(UserContext ctx)
    {
        string programs = Path.Combine(ctx.LocalAppData, "Programs", "Marflow Software");
        InstallParent = programs;
        InstallDefault = Path.Combine(programs, "TextTemplateManager");
        DataDir = Path.Combine(ctx.LocalAppData, "Marflow Software", "TextTemplateManager");
        LegacyDataDir = Path.Combine(ctx.LocalAppData, "Marflow Software", "TextTemplatesManager");
        StartMenuLnk = Path.Combine(ctx.StartMenuPrograms, "TextTemplateManager.lnk");
        DesktopLnk = Path.Combine(ctx.Desktop, "TextTemplateManager.lnk");
    }
}
