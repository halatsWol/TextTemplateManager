using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using TextTemplateManager.Common;

namespace TextTemplateManager.Models;

public partial class AppSettings : ObservableObject
{
    // Defaults ON; reconciled to the registry on startup (see DataNode.InitializeAsync).
    [ObservableProperty] private bool _runAtStartup = true;
    // Auto-update: check GitHub releases periodically and offer to install a newer version.
    [ObservableProperty] private bool _autoCheckUpdates = true;
    // When on, pre-release/beta versions (GitHub pre-releases, or tags with beta/preview/unstable/prev)
    // are also offered. Off by default — only stable releases are offered.
    [ObservableProperty] private bool _allowBetaUpdates = false;
    [ObservableProperty] private PasteMode _defaultPasteMode = PasteMode.Auto;
    [ObservableProperty] private string _pasteWindowHotkey = "Shift+Alt+Y";

    public ObservableCollection<SyncEntry> SyncEntries { get; set; } = new();
}

