using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TextTemplateManager.Common;
using TextTemplateManager.Helpers;
using TextTemplateManager.ViewModels;


namespace TextTemplateManager.Models
{
    /// <summary>A text template with optional single/multi-key shortcuts and tags.</summary>
    public partial class Template : BaseItem
    {
        [ObservableProperty]
        private string _content = string.Empty;

        [ObservableProperty]
        private string _singleKeyShortcut = string.Empty;

        [ObservableProperty]
        private string _multiKeyShortcut = string.Empty;

        [ObservableProperty]
        private string _tagsCsv = string.Empty;

        [ObservableProperty]
        private PasteMode _defaultPasteMode;

        [ObservableProperty]
        private bool _hasSingleKeyConflict;

        [ObservableProperty]
        private bool _hasMultiKeyConflict;

        /// <summary>Same single key used in another area — allowed, surfaced as a warning. Transient.</summary>
        [ObservableProperty]
        [property: JsonIgnore]
        private bool _hasSingleKeyCrossAreaWarning;

        /// <summary>Display-only: the multikey as typed (sync-prefixed, e.g. "and-msg").</summary>
        [ObservableProperty]
        [property: JsonIgnore]
        private string _effectiveMultiKey = string.Empty;

        /// <summary>Display-only: the parent root — a sync folder's name, or "local".</summary>
        [ObservableProperty]
        [property: JsonIgnore]
        private string _sourceLabel = string.Empty;

        /// <summary>Display-only: true when not inside a sync folder.</summary>
        [ObservableProperty]
        [property: JsonIgnore]
        private bool _isLocalSource;

        /// <summary>Owning ViewModel, required for conflict validation.</summary>
        [JsonIgnore]
        public MainViewModel? Owner { get; set; }

        private static readonly Regex _shortcutFilter = new(@"[^A-Z0-9+]", RegexOptions.Compiled);

        public Template()
        {
            ItemType = ItemType.Template;
            DefaultPasteMode = PasteMode.Auto;
        }

        #region Observable Property Handlers

        partial void OnSingleKeyShortcutChanged(string value)
        {
            NormalizeShortcut(ref _singleKeyShortcut, value);
            ValidateShortcuts();
        }

        partial void OnMultiKeyShortcutChanged(string value)
        {
            NormalizeShortcut(ref _multiKeyShortcut, value);
            ValidateShortcuts();
        }

        private void NormalizeShortcut(ref string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                field = string.Empty;
                return;
            }

            // trim, uppercase, remove invalid chars
            var normalized = _shortcutFilter.Replace(
                value.Trim().ToUpperInvariant(),
                string.Empty);

            if (normalized != value)
            {
                field = normalized;
                OnPropertyChanged();
            }
        }

        #endregion

        #region Shortcut Conflict Validation

        /// <summary>Flag this template's shortcut conflicts against the rest of the Owner's tree.</summary>
        public void ValidateShortcuts()
        {
            if (Owner == null) return;

            var otherTemplates = Owner.AllItems
                .SelectManyRecursive(i => i.Children)
                .OfType<Template>()
                .Where(t => t != this);

            HasSingleKeyConflict = !string.IsNullOrWhiteSpace(SingleKeyShortcut) &&
                                   otherTemplates.Any(t => string.Equals(t.SingleKeyShortcut, SingleKeyShortcut, StringComparison.OrdinalIgnoreCase));

            // Compare effective (prefixed) shortcuts, so same raw key in different folders is fine.
            string thisEffective = Owner.GetEffectiveMultiKey(this);
            HasMultiKeyConflict = !string.IsNullOrWhiteSpace(MultiKeyShortcut) &&
                                  otherTemplates.Any(t => string.Equals(Owner.GetEffectiveMultiKey(t), thisEffective, StringComparison.OrdinalIgnoreCase));
        }

        public void ForceRevalidate() => ValidateShortcuts();

        #endregion
    }
}
