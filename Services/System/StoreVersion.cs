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
    public static void Open() => Launch($@"shell:AppsFolder\{PackageFamilyName}!App");

    public static void OpenInStore() => Launch(StoreUri);

    /// <summary>Opens the Store page in the default browser.</summary>
    public static void OpenInBrowser() => Launch(WebUri);

    private static void Launch(string target)
    {
        try { using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>The notice about TextTemplateManager 2 is shown at every start until version 2 is installed
    /// or the user ticks "Don't show this again".</summary>
    internal static bool StoreNoticeDue(bool storeVersionInstalled, bool noticeHidden) =>
        !storeVersionInstalled && !noticeHidden;

    /// <summary>Whether a launch stays in the tray. A hidden autostart opens the window when there is
    /// something to show: a file to open, an unreadable data file, or the notice about version 2 (users
    /// starting hidden at login would never see it otherwise).</summary>
    internal static bool StartHidden(bool hiddenLaunch, bool openingFile, bool unreadableFiles, bool storeNoticeDue) =>
        hiddenLaunch && !openingFile && !unreadableFiles && !storeNoticeDue;

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
