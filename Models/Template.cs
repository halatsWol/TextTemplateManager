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

        /// <summary>Same effective multikey used in another area — allowed, surfaced as a warning. Transient.</summary>
        [ObservableProperty]
        [property: JsonIgnore]
        private bool _hasMultiKeyCrossAreaWarning;

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
                .Where(t => t != this)
                .ToList();

            Guid thisArea = Owner.GetAreaKey(this);

            // Single-key: same key in the SAME area is a conflict; the same key in another area is
            // allowed (local wins, then sync order) and only flagged as a warning.
            bool hasSingle = !string.IsNullOrWhiteSpace(SingleKeyShortcut);
            HasSingleKeyConflict = hasSingle && otherTemplates.Any(t =>
                Owner.GetAreaKey(t) == thisArea &&
                string.Equals(t.SingleKeyShortcut, SingleKeyShortcut, StringComparison.OrdinalIgnoreCase));
            HasSingleKeyCrossAreaWarning = hasSingle && !HasSingleKeyConflict && otherTemplates.Any(t =>
                Owner.GetAreaKey(t) != thisArea &&
                string.Equals(t.SingleKeyShortcut, SingleKeyShortcut, StringComparison.OrdinalIgnoreCase));

            // Multi-key compared on the effective (prefixed) shortcut, same rule as single-key: a
            // same-area clash is a conflict, across areas it's an allowed warning.
            string thisEffective = Owner.GetEffectiveMultiKey(this);
            bool hasMulti = !string.IsNullOrWhiteSpace(MultiKeyShortcut);
            HasMultiKeyConflict = hasMulti && otherTemplates.Any(t =>
                Owner.GetAreaKey(t) == thisArea &&
                string.Equals(Owner.GetEffectiveMultiKey(t), thisEffective, StringComparison.OrdinalIgnoreCase));
            HasMultiKeyCrossAreaWarning = hasMulti && !HasMultiKeyConflict && otherTemplates.Any(t =>
                Owner.GetAreaKey(t) != thisArea &&
                string.Equals(Owner.GetEffectiveMultiKey(t), thisEffective, StringComparison.OrdinalIgnoreCase));
        }

        public void ForceRevalidate() => ValidateShortcuts();

        #endregion
    }
}
