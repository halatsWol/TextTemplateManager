using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using TextTemplateManager.Common;

namespace TextTemplateManager.Models;

public partial class AppSettings : ObservableObject
{
    // Seeded from the OS autostart entry on first run (see DataNode.InitializeAsync) — off unless
    // the installer/registry already enabled it. Thereafter the settings file wins.
    [ObservableProperty] private bool _runAtStartup = false;
    // Auto-update: check GitHub releases periodically and offer to install a newer version.
    [ObservableProperty] private bool _autoCheckUpdates = true;
    // When on, pre-release/beta versions (GitHub pre-releases, or tags with beta/preview/unstable/prev)
    // are also offered. Off by default — only stable releases are offered.
    [ObservableProperty] private bool _allowBetaUpdates = false;
    [ObservableProperty] private PasteMode _defaultPasteMode = PasteMode.Auto;
    [ObservableProperty] private string _pasteWindowHotkey = "Shift+Alt+Y";

    public ObservableCollection<SyncEntry> SyncEntries { get; set; } = new();
}

