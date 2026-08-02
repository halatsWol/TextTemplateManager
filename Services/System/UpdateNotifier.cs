using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Threading.Tasks;

namespace TextTemplateManager.Services.System;

/// <summary>Windows toast notifications for updates (Windows App SDK). Entirely best-effort: any failure
/// is swallowed so the app never breaks if notifications aren't available. Button presses route back via
/// the <c>onAction</c> callback while the app is running (background/tray), marshalled to the UI thread.</summary>
public sealed class UpdateNotifier
{
    // One tag for every update toast: showing a new one replaces the previous instead of stacking, and
    // it gives the app a handle to clear a stale "Install now" whose installer is already gone.
    private const string NotificationTag = "ttm-update";

    private readonly DispatcherQueue _ui;
    private readonly Action<string> _onAction;
    private bool _registered;

    /// <summary>False when registration failed (an unpackaged app has no package identity, so the COM
    /// activator registration can fail). Callers must fall back to another channel when this is false.</summary>
    public bool IsAvailable => _registered;

    public UpdateNotifier(DispatcherQueue ui, Action<string> onAction)
    {
        _ui = ui;
        _onAction = onAction;
    }

    public void Register()
    {
        try
        {
            var mgr = AppNotificationManager.Default;
            mgr.NotificationInvoked += (s, e) =>
            {
                string action = e.Arguments.TryGetValue("action", out var a) ? a : "";
                _ui.TryEnqueue(() => { try { _onAction(action); } catch { } });
            };
            mgr.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Notifications stay unavailable and the app carries on (the in-app UI and the tray balloon
            // fallback both still work) — but record why. Swallowing this silently made a toast that never
            // appears impossible to diagnose anywhere the debugger isn't attached.
            Log($"registration failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        global::System.Diagnostics.Debug.WriteLine($"[Notifications] {message}");
        try
        {
            global::System.IO.File.AppendAllText(
                TextTemplateManager.Data.StorageService.GetCrashLogPath(),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [Notifications] {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    public void Unregister()
    {
        try { if (_registered) AppNotificationManager.Default.Unregister(); } catch { }
    }

    /// <summary>Interactive — stays on screen until acted on (auto-install off, update downloaded).</summary>
    public void ShowInstallReady(string version) => Show(b => b
        .AddText("Update ready")
        .AddText($"Version {version} is downloaded and ready to install.")
        .AddButton(new AppNotificationButton("Install now").AddArgument("action", "install"))
        .SetScenario(AppNotificationScenario.Reminder));

    /// <summary>Transient — auto-dismisses (auto-check off: an update was detected but not downloaded).</summary>
    public void ShowDownloadAvailable(string version) => Show(b => b
        .AddText("Update available")
        .AddText($"Version {version} is available to download.")
        .AddButton(new AppNotificationButton("Download now").AddArgument("action", "download")));

    /// <summary>Transient, informational — the app is auto-installing an update (auto-install on).</summary>
    public void ShowInstalling(string version) => Show(b => b
        .AddText("Installing update")
        .AddText($"Version {version} is being installed. The app will restart."));

    /// <summary>Interactive — automatic installs were given up on for this release, so the user decides.</summary>
    public void ShowAutoInstallFailed(string version) => Show(b => b
        .AddText("Update needs your attention")
        .AddText($"Version {version} couldn't be installed automatically. Install it yourself, or open the app to review.")
        .AddButton(new AppNotificationButton("Install now").AddArgument("action", "install"))
        .AddButton(new AppNotificationButton("Open app").AddArgument("action", "open"))
        .SetScenario(AppNotificationScenario.Reminder));

    /// <summary>Removes any update toast still on screen (its installer may no longer exist).</summary>
    public async Task ClearAsync()
    {
        try { if (_registered) await AppNotificationManager.Default.RemoveByTagAsync(NotificationTag); }
        catch { }
    }

    private void Show(Func<AppNotificationBuilder, AppNotificationBuilder> build)
    {
        if (!_registered) return;
        try
        {
            var notification = build(new AppNotificationBuilder()).BuildNotification();
            notification.Tag = NotificationTag;
            AppNotificationManager.Default.Show(notification);
        }
        catch { }
    }
}
