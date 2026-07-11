using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using TextTemplateManager.Common;

namespace TextTemplateManager.Models
{
    [JsonDerivedType(typeof(Folder), typeDiscriminator: "folder")]
    [JsonDerivedType(typeof(Template), typeDiscriminator: "template")]
    public abstract partial class BaseItem : ObservableObject
    {
        [ObservableProperty]
        private string _title = string.Empty;

        // TreeView expansion state. [property: JsonIgnore] targets the GENERATED property; a plain
        // [JsonIgnore] would only hit the field, leaking expansion state into data.ttmdata.
        [ObservableProperty]
        [property: JsonIgnore]
        private bool _isExpanded;
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid? ParentId { get; set; }

        /// <summary>For synced items: the Id as stored in the sync file (null for local items).
        /// <see cref="Id"/> stays the app-wide id; SyncId maps back to the shared file's entry.</summary>
        public Guid? SyncId { get; set; }
        public ItemType ItemType { get; set; }
        public string LastChange { get; set; } = DateTime.Now.ToString("yyyyMMddHHmmss");

        public ObservableCollection<BaseItem> Children { get; set; } = new();

        /// <summary>Display-only projection of <see cref="Children"/> the TreeView binds to:
        /// mirrors Children, or holds only matches during a search. Never persisted.</summary>
        [JsonIgnore]
        public ObservableCollection<BaseItem> ViewChildren { get; } = new();

        /// <summary>Top-level folder representing a sync source. Transient; drives the icon badge.</summary>
        [JsonIgnore]
        public bool IsSyncRoot { get; set; }

        /// <summary>Sync-root folder whose file is missing; cached content still shown. Transient.</summary>
        [JsonIgnore]
        public bool SyncFileMissing { get; set; }

        // Base glyph only (Folder/Document). Sync roots overlay a small sync badge in the tree.
        [JsonIgnore]
        public Symbol Icon => this is Folder ? Symbol.Folder : Symbol.Document;
    }
}