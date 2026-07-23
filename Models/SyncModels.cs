using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;

namespace TextTemplateManager.Models
{
    /// <summary>A configured sync source (a shared .ttmdata file shown as a pinned folder).</summary>
    public partial class SyncSource : ObservableObject
    {
        /// <summary>Stable app-side Id of this source's root folder, kept across reorders/relaunches.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string _path = string.Empty;

        /// <summary>Inactive sources aren't loaded (cached data dropped).</summary>
        [ObservableProperty] private bool _isActive = true;

        /// <summary>Off = read-only (nothing written back).</summary>
        [ObservableProperty] private bool _allowSave = false;

        /// <summary>Namespaces this folder's multikey shortcuts (e.g. "and" → "and-msg").</summary>
        [ObservableProperty] private string _shortcutPrefix = string.Empty;

        /// <summary>Path configured but file absent (moved/offline). Not persisted.</summary>
        [JsonIgnore]
        public bool IsFileMissing => !string.IsNullOrWhiteSpace(Path) && !File.Exists(Path);

        partial void OnPathChanged(string value) => OnPropertyChanged(nameof(IsFileMissing));

        /// <summary>Reorder-arrow affordances, set by the Sync settings page from list position:
        /// the top row can't move up, the bottom can't move down. Transient, not persisted.</summary>
        [ObservableProperty]
        [property: JsonIgnore]
        private bool _canMoveUp = true;

        [ObservableProperty]
        [property: JsonIgnore]
        private bool _canMoveDown = true;
    }

    /// <summary>Persisted sync configuration (sync.ttmsettings).</summary>
    public partial class SyncSettings : ObservableObject
    {
        /// <summary>Joins a folder's prefix to a template's multikey (e.g. "and" + "-" + "msg").</summary>
        [ObservableProperty] private string _separator = "-";

        /// <summary>Sources in display/priority order (index 0 = topmost).</summary>
        public ObservableCollection<SyncSource> Sources { get; set; } = new();
    }
}
