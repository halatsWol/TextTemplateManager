using System;
// Imported rather than qualified: this namespace itself ends in ".System", so a written-out
// "System.IO.Path" would bind to TextTemplateManager.Services.System.IO and fail to compile.
using System.IO;
using System.Threading.Tasks;

namespace TextTemplateManager.Services.System;

public enum UpdateStatusKind { None, Downloading, Available, Ready }

/// <summary>What set a check going. Only <see cref="User"/> — a click on visible UI — may raise dialogs;
/// <see cref="Auto"/> is the silent periodic check, and <see cref="Toast"/> came from a notification
/// button while the window is hidden, so it must report back the same way.</summary>
public enum UpdateTrigger { Auto, User, Toast }

public enum UpdateNote { DownloadAvailable, InstallReady, AutoInstallFailed }

/// <summary>Everything the update state machine needs from the running app. MainPage implements this
/// against WinUI and Win32; the test suite implements it with a fake, which is the only way this logic
/// can be exercised at all — the real one needs a UI thread.</summary>
public interface IUpdateHost
{
    // ---- policy and settings ----
    bool UpdatesAllowed { get; }
    bool BetaAllowed { get; }
    bool AutoCheckUpdates { get; }
    bool AutoInstallUpdates { get; }
    bool AllowBetaUpdates { get; }

    // ---- release feed and staged installers ----
    Task<UpdateService.UpdateInfo?> CheckAsync(bool allowBeta);
    Task<string?> EnsureDownloadedAsync(UpdateService.UpdateInfo info);
    string InstallerDir { get; }
    bool HasUsableInstaller(string path);
    void CleanInstallerDir(string? keep);
    string InstalledVersion { get; }
    string AppVersion { get; }

    // ---- environment ----
    bool WindowVisible { get; }
    bool WindowForeground { get; }
    bool PasteWindowVisible { get; }
    uint IdleSeconds { get; }
    bool WritesQuiet { get; }

    // ---- presentation ----
    void SetStatus(UpdateStatusKind kind, string busyText);
    /// <summary>A check is running: the "Update available" button starts a download, so it must not stay
    /// clickable during one. Distinct from <see cref="SetStatus"/>, which owns what the strip shows.</summary>
    void SetBusy(bool busy);
    void FlashTaskbar();
    bool NotificationsAvailable { get; }
    void ShowToast(UpdateNote note, string versionLabel);
    void ShowInstallingToast(string versionLabel);
    void ShowTrayBalloon(string title, string message);
    Task ClearNotificationsAsync();
    /// <summary>Modal install prompt. Returns true when the user chose to install now.</summary>
    Task<bool> PromptInstallAsync(string versionLabel, bool autoFailed);
    Task ShowMessageAsync(string title, string message);
    void SurfaceMainWindow();

    // ---- installing ----
    Task PersistPendingWorkAsync();
    Task DrainWritesAsync();
    bool LaunchInstaller(string path, bool relaunchHidden);
    void Shutdown();
}

/// <summary>The update state machine: detection, notification routing, and unattended installation.
///
/// Deliberately free of WinUI so it can be tested. Every environmental fact arrives through
/// <see cref="IUpdateHost"/>; nothing here touches a window, a dialog or the registry directly.</summary>
public sealed class UpdateCoordinator
{
    /// <summary>An unattended install waits for real idle: no input anywhere in the session for this
    /// long. The window merely being in the background is not enough — this app lives in the tray, so
    /// that is its normal state and would mean installing while the user works in another app.</summary>
    public const uint SafeIdleSeconds = 300;

    private readonly IUpdateHost _host;

    public UpdateCoordinator(IUpdateHost host) => _host = host;

    // ---- observable state (also what the tests assert against) ----

    /// <summary>Downloaded installer for the known release, or null when nothing is staged.</summary>
    public string? ReadyInstallerPath { get; private set; }
    /// <summary>Release tag as shown to the user (leading "v" stripped).</summary>
    public string? ReadyVersionLabel { get; private set; }
    /// <summary>Raw tag of the release currently known, used to tell a genuinely new release apart.</summary>
    public string? KnownTag { get; private set; }
    /// <summary>Installer armed for an unattended install, waiting for a safe moment.</summary>
    public string? PendingAutoInstallPath { get; private set; }
    /// <summary>Automatic attempts for this release are used up; the user has to decide.</summary>
    public bool AutoInstallBlocked { get; private set; }
    public bool Busy { get; private set; }

    // ---- tag helper ----

    /// <summary>Tag as shown to the user — drop a leading "v" so it reads "Version 0.9.6-beta".</summary>
    public static string DisplayTag(string tag) => string.IsNullOrWhiteSpace(tag) ? "" : tag.TrimStart('v', 'V');

    // ---- detection ----

    public async Task RunCheckAsync(UpdateTrigger trigger)
    {
        bool byUser = trigger == UpdateTrigger.User;

        // Enterprise policy (registry) can hard-disable updates for everyone: no check, no indicators.
        if (!_host.UpdatesAllowed)
        {
            if (byUser) await _host.ShowMessageAsync("Check for Updates", "Updates are disabled by your organization.");
            return;
        }
        if (Busy) return;
        SetBusy(true);
        try
        {
            bool allowBeta = _host.AllowBetaUpdates && _host.BetaAllowed;

            UpdateService.UpdateInfo? info;
            try { info = await _host.CheckAsync(allowBeta); }
            catch
            {
                if (byUser) await _host.ShowMessageAsync("Check for Updates", "Could not reach the update server.");
                return;
            }

            if (info == null)
            {
                await ResetToUpToDateAsync();
                if (byUser) await _host.ShowMessageAsync("You're up to date", $"Version {_host.AppVersion} is the latest.");
                return;
            }

            // Compare tags, not numbers: a stable release and its own beta share a numeric version, so a
            // numeric comparison would treat the stable follow-up as already seen and never announce it.
            bool firstSeen = !string.Equals(KnownTag, info.Tag, StringComparison.OrdinalIgnoreCase);
            if (firstSeen) PendingAutoInstallPath = null;
            KnownTag = info.Tag;
            string versionLabel = DisplayTag(info.Tag);
            ReadyVersionLabel = versionLabel;

            // Read "we gave up on installing this one" off disk rather than trusting a field: it has to
            // survive a restart, and a fresh process must not quietly earn the release another attempt.
            AutoInstallBlocked = UpdateState.Load() is { Exhausted: true } spent
                                 && string.Equals(spent.Tag, info.Tag, StringComparison.OrdinalIgnoreCase);

            // Asset names carry the version, so a file already staged belongs to exactly this release.
            string localPath = Path.Combine(_host.InstallerDir, info.AssetName);
            bool haveLocal = _host.HasUsableInstaller(localPath);

            // Auto-download off: detect and surface only — but never discard an installer already on disk
            // for this release, or a manual download would be thrown away by the next background check.
            if (trigger == UpdateTrigger.Auto && !_host.AutoCheckUpdates)
            {
                ReadyInstallerPath = haveLocal ? localPath : null;
                _host.SetStatus(haveLocal ? UpdateStatusKind.Ready : UpdateStatusKind.Available, "");
                if (firstSeen)
                    await AnnounceAsync(haveLocal ? UpdateNote.InstallReady : UpdateNote.DownloadAvailable,
                                        versionLabel, haveLocal ? localPath : null);
                return;
            }

            if (!haveLocal) _host.SetStatus(UpdateStatusKind.Downloading, "Downloading update…");
            string? path;
            try { path = await _host.EnsureDownloadedAsync(info); }
            catch
            {
                _host.SetStatus(UpdateStatusKind.None, "");
                if (byUser) await _host.ShowMessageAsync("Update", "Found an update but couldn't download it.");
                return;
            }
            if (path == null) { _host.SetStatus(UpdateStatusKind.None, ""); return; }

            ReadyInstallerPath = path;
            _host.SetStatus(UpdateStatusKind.Ready, "");

            if (byUser) { await PromptAndInstallAsync(versionLabel, path, autoFailed: false); return; }

            if (_host.AutoInstallUpdates && !AutoInstallBlocked)
            {
                ArmAutoInstall(path, info.Tag);
                await TryAutoInstallAsync();     // go now if the user already happens to be idle
                return;
            }

            // A toast-triggered download always reports back, otherwise the user taps "Download now" and
            // nothing visible happens (the release is no longer "first seen" by then).
            if (firstSeen || AutoInstallBlocked || trigger == UpdateTrigger.Toast)
                await AnnounceAsync(AutoInstallBlocked ? UpdateNote.AutoInstallFailed : UpdateNote.InstallReady,
                                    versionLabel, path);
        }
        finally { SetBusy(false); }
    }

    private async Task ResetToUpToDateAsync()
    {
        KnownTag = null;
        ReadyInstallerPath = null;
        ReadyVersionLabel = null;
        PendingAutoInstallPath = null;
        AutoInstallBlocked = false;
        UpdateState.Clear();
        _host.CleanInstallerDir(null);
        await _host.ClearNotificationsAsync();
        _host.SetStatus(UpdateStatusKind.None, "");
    }

    // ---- unattended install ----

    /// <summary>Arms an unattended install. The target is recorded before any attempt, so a release that
    /// fails to apply is retried a bounded number of times and then handed to the user.</summary>
    public void ArmAutoInstall(string path, string tag)
    {
        PendingAutoInstallPath = path;
        var state = UpdateState.Load();
        if (state == null || !string.Equals(state.Tag, tag, StringComparison.OrdinalIgnoreCase))
            state = new UpdateState { Tag = tag };
        state.Asset = Path.GetFileName(path);
        state.FromVersion = _host.InstalledVersion;
        state.Save();
    }

    /// <summary>Installs an armed update if this is genuinely a safe moment: no input anywhere for a few
    /// minutes, the window not in use, no Quick Paste window on screen, and nothing being written.
    /// Otherwise it waits — and if the app exits first, <see cref="ResumeDeferredInstallAsync"/> picks it
    /// up at the next launch.</summary>
    public async Task TryAutoInstallAsync()
    {
        if (PendingAutoInstallPath is not string path) return;
        if (!_host.AutoInstallUpdates) return;
        if (!_host.HasUsableInstaller(path)) { PendingAutoInstallPath = null; return; }

        // Checked before the idle gates: having given up is news the user needs now, not whenever they
        // next happen to leave the machine alone for five minutes.
        if (UpdateState.Load() is { Exhausted: true })
        {
            PendingAutoInstallPath = null;
            AutoInstallBlocked = true;
            await AnnounceAsync(UpdateNote.AutoInstallFailed, ReadyVersionLabel ?? "", path);
            return;
        }

        if (_host.IdleSeconds < SafeIdleSeconds) return;
        if (_host.WindowForeground) return;
        if (_host.PasteWindowVisible) return;
        if (!_host.WritesQuiet) return;              // a sync poll is mid-write; try again next tick

        PendingAutoInstallPath = null;
        _host.ShowInstallingToast(ReadyVersionLabel ?? "");
        await InstallAsync(path, unattended: true, flush: true, relaunchHidden: !_host.WindowVisible);
    }

    /// <summary>Picks up an unattended install armed in an earlier session that exited before a safe
    /// moment came. Runs regardless of how this launch happened — a login autostart or the user opening
    /// the app — because startup is the least disruptive point to swap the app out.</summary>
    public async Task ResumeDeferredInstallAsync()
    {
        var state = UpdateState.Load();
        if (state == null) return;

        // The running version is no longer the one the attempt started from: the update applied.
        if (!string.Equals(state.FromVersion, _host.InstalledVersion, StringComparison.OrdinalIgnoreCase))
        {
            UpdateState.Clear();
            _host.CleanInstallerDir(null);
            return;
        }

        // Same version after an attempt means the install didn't take. Stop at the cap and let the
        // periodic check surface it to the user, instead of reinstalling on every single launch forever.
        if (state.Exhausted) { AutoInstallBlocked = true; return; }
        if (!_host.AutoInstallUpdates) return;

        string path = Path.Combine(_host.InstallerDir, state.Asset);
        if (!_host.HasUsableInstaller(path)) { UpdateState.Clear(); return; }

        ReadyVersionLabel = DisplayTag(state.Tag);
        KnownTag = state.Tag;
        ReadyInstallerPath = path;
        await InstallAsync(path, unattended: true, flush: false, relaunchHidden: LaunchedHidden);
    }

    /// <summary>Set by the host before <see cref="ResumeDeferredInstallAsync"/> so a login autostart comes
    /// back to the tray while a user-opened app comes back on screen.</summary>
    public bool LaunchedHidden { get; set; }

    // ---- settings changes ----

    /// <summary>Turning auto-install off must also cancel an install already armed; turning it on should
    /// pick up an update that is already downloaded instead of waiting for the next check.</summary>
    public async Task OnAutoInstallSettingChangedAsync()
    {
        if (!_host.AutoInstallUpdates)
        {
            PendingAutoInstallPath = null;
            UpdateState.Clear();
            return;
        }
        if (AutoInstallBlocked || ReadyInstallerPath is not string path || KnownTag is not string tag) return;
        ArmAutoInstall(path, tag);
        await TryAutoInstallAsync();
    }

    // ---- notification callbacks ----

    public async Task OnNotificationActionAsync(string action)
    {
        switch (action)
        {
            case "install":
                if (ReadyInstallerPath is string p)
                    await InstallAsync(p, unattended: false, flush: true, relaunchHidden: !_host.WindowVisible);
                else
                    await RunCheckAsync(UpdateTrigger.Toast);   // installer was cleaned up meanwhile
                break;
            case "download":
                await RunCheckAsync(UpdateTrigger.Toast);
                break;
            case "open":
                _host.SurfaceMainWindow();
                break;
        }
    }

    // ---- announcing ----

    /// <summary>Puts an update message where the user will actually see it: a dialog when the window is on
    /// screen, a toast when it isn't, and a tray balloon when toast registration failed (which it can, for
    /// an unpackaged app) — otherwise a tray-only session would be told about updates nowhere at all.</summary>
    public async Task AnnounceAsync(UpdateNote note, string versionLabel, string? installerPath)
    {
        if (_host.WindowVisible)
        {
            // On screen but behind another app — flash the taskbar so it isn't missed.
            if (!_host.WindowForeground) _host.FlashTaskbar();

            // A passive detection needs no dialog: the top-right button already says it.
            if (note == UpdateNote.DownloadAvailable) return;
            if (installerPath != null)
                await PromptAndInstallAsync(versionLabel, installerPath, note == UpdateNote.AutoInstallFailed);
            return;
        }

        if (_host.NotificationsAvailable) { _host.ShowToast(note, versionLabel); return; }

        _host.ShowTrayBalloon(
            note == UpdateNote.AutoInstallFailed ? "Update needs your attention" : "Update available",
            note switch
            {
                UpdateNote.DownloadAvailable => $"Version {versionLabel} is available to download.",
                UpdateNote.InstallReady => $"Version {versionLabel} is ready to install.",
                _ => $"Version {versionLabel} couldn't be installed automatically.",
            });
    }

    // ---- installing ----

    public async Task PromptAndInstallAsync(string versionLabel, string installerPath, bool autoFailed)
    {
        if (await _host.PromptInstallAsync(versionLabel, autoFailed))
            await InstallAsync(installerPath, unattended: false, flush: true, relaunchHidden: false);
    }

    /// <param name="unattended">No user is watching: report failures through a notification, and count the
    /// attempt so a release that refuses to apply stops being retried.</param>
    /// <param name="flush">Persist pending edits first. Skipped at launch, where there is nothing to flush.</param>
    /// <param name="relaunchHidden">Bring the app back in the tray instead of opening its window.</param>
    public async Task InstallAsync(string installerPath, bool unattended, bool flush, bool relaunchHidden)
    {
        if (!_host.HasUsableInstaller(installerPath))
        {
            // Superseded by a newer release and cleaned up between arming and installing.
            ReadyInstallerPath = null;
            PendingAutoInstallPath = null;
            _host.SetStatus(UpdateStatusKind.None, "");
            if (!unattended)
                await _host.ShowMessageAsync("Update", "The downloaded installer is no longer available. Check for updates again.");
            return;
        }

        _host.SetStatus(UpdateStatusKind.Downloading, "Updating…");   // reuse the busy indicator

        // Count the attempt before launching, not after: if the install doesn't take, the app is already
        // gone by the time that could be observed, so the record has to be on disk beforehand.
        if (unattended && UpdateState.Load() is { } state)
        {
            state.Attempts++;
            state.Save();
        }

        if (flush) await _host.PersistPendingWorkAsync();

        // Let anything still writing finish. The installer closes the app, so a sync write cut off here
        // is what leaves a truncated file or a cloud conflict copy behind.
        await _host.DrainWritesAsync();

        if (_host.LaunchInstaller(installerPath, relaunchHidden))
        {
            _host.Shutdown();
            return;
        }

        _host.SetStatus(UpdateStatusKind.Ready, "");
        if (unattended)
        {
            AutoInstallBlocked = true;
            await AnnounceAsync(UpdateNote.AutoInstallFailed, ReadyVersionLabel ?? "", installerPath);
        }
        else await _host.ShowMessageAsync("Update", "Could not start the installer.");
    }

    private void SetBusy(bool busy)
    {
        Busy = busy;
        _host.SetBusy(busy);
    }
}
