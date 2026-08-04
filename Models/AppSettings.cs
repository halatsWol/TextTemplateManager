using CommunityToolkit.Mvvm.ComponentModel;
using TextTemplateManager.Common;

namespace TextTemplateManager.Models;

public partial class AppSettings : ObservableObject
{
    // Auto-update: check GitHub releases periodically and download a newer version when found. With this
    // off the app still detects and notifies about an update, but doesn't download it automatically.
    [ObservableProperty] private bool _autoCheckUpdates = true;
    // When on, a downloaded update is installed automatically at a safe moment (app not in use), no prompt.
    // Off (default): the user confirms each install. Only relevant while AutoCheckUpdates is on.
    [ObservableProperty] private bool _autoInstallUpdates = false;
    // Windows notifications about updates (and the tray-balloon fallback). Off suppresses them entirely —
    // an update waiting while the window is hidden is then only visible after opening the window.
    // Named for the category rather than notifications in general, so further categories (and a parent
    // "show notifications" switch governing all of them) can be added without this becoming ambiguous.
    [ObservableProperty] private bool _showUpdateNotifications = true;
    // When on, pre-release/beta versions (GitHub pre-releases, or tags with beta/preview/unstable/prev)
    // are also offered. Off by default — only stable releases are offered.
    [ObservableProperty] private bool _allowBetaUpdates = false;
    [ObservableProperty] private PasteMode _defaultPasteMode = PasteMode.Auto;
    [ObservableProperty] private string _pasteWindowHotkey = "Shift+Alt+Y";

    // When on, hides the dismissible cross-area shortcut warnings (the same shortcut used in local +
    // sync, or across sync folders). Same-area duplicate conflicts still block. Off by default.
    [ObservableProperty] private bool _hideCrossAreaShortcutWarnings = false;

    // Local loopback connector for browser extensions. Off by default (opens a 127.0.0.1 port);
    // the token is generated when first enabled and required on every request.
    [ObservableProperty] private bool _browserConnectorEnabled = false;
    [ObservableProperty] private int _browserConnectorPort = 47615;
    [ObservableProperty] private string _browserConnectorToken = "";
}

