using TextTemplateManager.Services.System;

namespace TextTemplateManager.Tests;

/// <summary>Stands in for the running app so the update state machine can be driven directly: every
/// environmental fact (idle time, window visibility, whether toasts registered, whether the installer
/// launches) becomes a field to set, and every user-visible effect is recorded for assertion.</summary>
internal sealed class FakeUpdateHost : IUpdateHost
{
    public FakeUpdateHost(string installerDir) => InstallerDir = installerDir;

    // ---- policy and settings ----
    public bool UpdatesAllowed { get; set; } = true;
    public bool BetaAllowed { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; }
    public bool AllowBetaUpdates { get; set; }

    // ---- release feed ----
    /// <summary>What the next check returns. Null means "up to date".</summary>
    public UpdateService.UpdateInfo? Available { get; set; }
    /// <summary>When set, the check throws it — the unreachable-server path.</summary>
    public Exception? CheckThrows { get; set; }
    /// <summary>When set, the download throws it.</summary>
    public Exception? DownloadThrows { get; set; }
    /// <summary>When true, the download completes but yields nothing.</summary>
    public bool DownloadReturnsNull { get; set; }
    public int ChecksRun { get; private set; }
    public int DownloadsRun { get; private set; }

    public string InstallerDir { get; }
    public string InstalledVersion { get; set; } = "1.3.1";
    public string AppVersion { get; set; } = "1.3.1";

    /// <summary>When set, a check parks here until the test completes it — the only way to hold one
    /// check genuinely in flight while a second is attempted.</summary>
    public TaskCompletionSource? CheckGate { get; set; }

    public async Task<UpdateService.UpdateInfo?> CheckAsync(bool allowBeta)
    {
        ChecksRun++;
        BetaRequested = allowBeta;
        if (CheckGate != null) await CheckGate.Task;
        if (CheckThrows != null) throw CheckThrows;
        return Available;
    }

    public bool BetaRequested { get; private set; }

    public Task<string?> EnsureDownloadedAsync(UpdateService.UpdateInfo info)
    {
        DownloadsRun++;
        if (DownloadThrows != null) throw DownloadThrows;
        if (DownloadReturnsNull) return Task.FromResult<string?>(null);

        string path = Path.Combine(InstallerDir, info.AssetName);
        Directory.CreateDirectory(InstallerDir);
        File.WriteAllText(path, "installer");
        return Task.FromResult<string?>(path);
    }

    public bool HasUsableInstaller(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    public List<string?> CleanCalls { get; } = new();
    public void CleanInstallerDir(string? keep) => CleanCalls.Add(keep);

    // ---- environment ----
    public bool WindowVisible { get; set; }
    public bool WindowForeground { get; set; }
    public bool PasteWindowVisible { get; set; }
    public uint IdleSeconds { get; set; }
    public bool WritesQuiet { get; set; } = true;

    // ---- recorded presentation ----
    public UpdateStatusKind Status { get; private set; } = UpdateStatusKind.None;
    public string BusyText { get; private set; } = "";
    public List<UpdateStatusKind> StatusHistory { get; } = new();
    public void SetStatus(UpdateStatusKind kind, string busyText)
    {
        Status = kind;
        if (kind == UpdateStatusKind.Downloading) BusyText = busyText;
        StatusHistory.Add(kind);
    }

    public bool BusySet { get; private set; }
    public void SetBusy(bool busy) => BusySet = busy;

    public int TaskbarFlashes { get; private set; }
    public void FlashTaskbar() => TaskbarFlashes++;

    public bool NotificationsAvailable { get; set; } = true;
    public List<UpdateNote> Toasts { get; } = new();
    public void ShowToast(UpdateNote note, string versionLabel) => Toasts.Add(note);

    public List<string> InstallingToasts { get; } = new();
    public void ShowInstallingToast(string versionLabel) => InstallingToasts.Add(versionLabel);

    public List<string> Balloons { get; } = new();
    public void ShowTrayBalloon(string title, string message) => Balloons.Add(title);

    public int NotificationsCleared { get; private set; }
    public Task ClearNotificationsAsync() { NotificationsCleared++; return Task.CompletedTask; }

    /// <summary>What the modal prompt returns — true means the user chose "Install now".</summary>
    public bool PromptAnswer { get; set; }
    public List<(string Version, bool AutoFailed)> Prompts { get; } = new();
    public Task<bool> PromptInstallAsync(string versionLabel, bool autoFailed)
    {
        Prompts.Add((versionLabel, autoFailed));
        return Task.FromResult(PromptAnswer);
    }

    public List<string> Messages { get; } = new();
    public Task ShowMessageAsync(string title, string message) { Messages.Add(title); return Task.CompletedTask; }

    public int WindowSurfaced { get; private set; }
    public void SurfaceMainWindow() => WindowSurfaced++;

    // ---- installing ----
    public int PersistCalls { get; private set; }
    public Task PersistPendingWorkAsync() { PersistCalls++; return Task.CompletedTask; }

    public int DrainCalls { get; private set; }
    public Task DrainWritesAsync() { DrainCalls++; return Task.CompletedTask; }

    /// <summary>Whether the installer process starts. False models a launch failure.</summary>
    public bool InstallerLaunches { get; set; } = true;
    public List<(string Path, bool Hidden)> Launches { get; } = new();
    public bool LaunchInstaller(string path, bool relaunchHidden)
    {
        Launches.Add((path, relaunchHidden));
        return InstallerLaunches;
    }

    public int Shutdowns { get; private set; }
    public void Shutdown() => Shutdowns++;

    // ---- helpers ----

    /// <summary>Puts the app in the state an unattended install is allowed to fire from.</summary>
    public FakeUpdateHost AtSafeMoment()
    {
        IdleSeconds = UpdateCoordinator.SafeIdleSeconds + 1;
        WindowForeground = false;
        WindowVisible = false;
        PasteWindowVisible = false;
        WritesQuiet = true;
        return this;
    }

    public static UpdateService.UpdateInfo Release(string tag = "v1.3.2", string? asset = null) =>
        new(Version.Parse(tag.TrimStart('v').Split('-')[0]),
            tag,
            $"https://example/{asset ?? $"TextTemplateManager-Setup-{tag.TrimStart('v')}.exe"}",
            asset ?? $"TextTemplateManager-Setup-{tag.TrimStart('v')}.exe");
}
