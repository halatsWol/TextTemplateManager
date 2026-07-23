using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using TextTemplateManager.Common;

namespace TextTemplateManager.Models;

public partial class AppSettings : ObservableObject
{
    // Default off; seeded from the OS autostart entry on first run (see DataNode.InitializeAsync).
    [ObservableProperty] private bool _runAtStartup = false;
    // Auto-update: check GitHub releases periodically and offer to install a newer version.
    [ObservableProperty] private bool _autoCheckUpdates = true;
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

    public ObservableCollection<SyncEntry> SyncEntries { get; set; } = new();
}

