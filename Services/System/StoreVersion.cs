using System.Diagnostics;
using System.Linq;
using Windows.Management.Deployment;

namespace TextTemplateManager.Services.System;

/// <summary>TextTemplateManager 2, the Microsoft Store version that replaces this one.</summary>
public static class StoreVersion
{
    public const string PackageFamilyName = "MarflowSoftware.TextTemplateManager_eb1r5ey0kd4ny";
    public const string StoreUri = "ms-windows-store://pdp/?ProductId=9NMBSZLLLN6V";
    public const string WebUri = "https://apps.microsoft.com/detail/9NMBSZLLLN6V";

    /// <summary>Starts TextTemplateManager 2.</summary>
    public static void Open()
    {
        try { using var _ = Process.Start(new ProcessStartInfo($@"shell:AppsFolder\{PackageFamilyName}!App") { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>True when TextTemplateManager 2 is installed for the current user.</summary>
    public static bool IsInstalled() => IsPackageInstalled(PackageFamilyName);

    internal static bool IsPackageInstalled(string packageFamilyName)
    {
        try { return new PackageManager().FindPackagesForUser(string.Empty, packageFamilyName).Any(); }
        catch { return false; }
    }

    /// <summary>A hidden autostart (Windows login) of this version has nothing to do once version 2 is
    /// installed: version 2 closes it anyway, so it leaves without a window or tray icon.</summary>
    internal static bool ExitAtStartup(bool storeVersionInstalled, bool hiddenLaunch, bool openingFile) =>
        storeVersionInstalled && hiddenLaunch && !openingFile;
}
