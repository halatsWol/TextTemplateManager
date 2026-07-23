using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using TextTemplateManager.Common;
using TextTemplateManager.Data;
using TextTemplateManager.Helpers;
using TextTemplateManager.Models;
using Windows.Storage.Pickers;


namespace TextTemplateManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public List<PasteMode> PasteModes { get; } = PasteModeLabel.DisplayOrder.ToList();
    private readonly DataNode _dataNode;
    private DispatcherQueue _ui;
    private DispatcherQueueTimer _syncPollTimer = null!;   // set in the constructor's timer setup

    public ObservableCollection<BaseItem> AllItems => _dataNode.LocalItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorEnabled))]
    [NotifyPropertyChangedFor(nameof(SelectedTemplate))]
    [NotifyPropertyChangedFor(nameof(IsFolderSelected))]
    [NotifyPropertyChangedFor(nameof(IsNoSelection))]
    [NotifyPropertyChangedFor(nameof(IsSelectedReadOnly))]
    [NotifyPropertyChangedFor(nameof(IsSelectedEditable))]
    private BaseItem? _selectedItem;

    public bool IsFolderSelected => SelectedItem is Folder;
    public bool IsNoSelection => SelectedItem is null;

    public bool IsSelectedReadOnly => SelectedItem != null && _dataNode.IsItemReadOnly(SelectedItem);
    public bool IsSelectedEditable => !IsSelectedReadOnly;

    [ObservableProperty]
    private ObservableCollection<BaseItem> _rootNodes = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _includeContentSearch;
    [ObservableProperty] private bool _strictSearch;

    public bool IsEditorEnabled => SelectedItem is Template;

    // Null when a Folder is selected — avoids cast exceptions in XAML.
    public Template? SelectedTemplate => SelectedItem as Template;

    public MainViewModel()
    {
        // Shared singleton so editor, PasteWindow and Preferences share one in-memory tree.
        _dataNode = DataNode.Instance;
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Re-project on sync re-apply; re-select if the refresh dropped the selection.
        _dataNode.TreeChanged += () => _ui?.TryEnqueue(() =>
        {
            var prev = SelectedItem;
            // A sync-folder reorder is the one refresh that needs a top-level Move. WinUI's TreeView
            // crashes on an in-place Move of its bound root collection (the reason CloseSettings uses
            // ReloadTree), so rebind the whole collection in that case; otherwise refresh in place to
            // keep expansion state for the frequent content-only refreshes (sync poll, connector add).
            if (RootOrderChanged())
                ReloadTree();
            else
                ApplyFilter();
            if (prev != null && !ReferenceEquals(SelectedItem, prev) && Flatten(AllItems).Contains(prev))
                SelectedItem = prev;
        });

        _ = Task.Run(async () =>
        {
            await _dataNode.InitializeAsync();
            _ui?.TryEnqueue(() =>
            {
                ApplyFilter();
                StartSyncPolling();
            });

            foreach (var t in Flatten(AllItems).OfType<Template>())
            {
                t.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(Template.SingleKeyShortcut)
                        or nameof(Template.MultiKeyShortcut))
                    {
                        ValidateAllShortcuts();
                    }
                };
            }

        });
    }

    // Poll sync files for external edits (timestamps only; reconcile on real change).
    private void StartSyncPolling()
    {
        _syncPollTimer = _ui.CreateTimer();
        _syncPollTimer.Interval = TimeSpan.FromSeconds(4);
        _syncPollTimer.Tick += async (s, e) =>
        {
            try { await _dataNode.CheckSyncFilesForChangesAsync(); } catch { }
        };
        _syncPollTimer.Start();
    }

    #region Search Logic

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnIncludeContentSearchChanged(bool value) => ApplyFilter();
    partial void OnStrictSearchChanged(bool value) => ApplyFilter();

    /// <summary>Full tree rebuild with a fresh RootNodes instance. TreeView does not reliably
    /// reflect a Move on its bound collection (e.g. reordered sync folders), so a reload re-binds
    /// the whole collection instead of moving items in place.</summary>
    public void ReloadTree()
    {
        RootNodes = new ObservableCollection<BaseItem>();
        ApplyFilter();
    }

    /// <summary>True when the data model's top-level items match RootNodes as a set but in a
    /// different order — the only refresh needing an in-place root Move, which the TreeView
    /// mishandles. Add/remove (count differs) and filtering are refreshed in place safely.</summary>
    private bool RootOrderChanged()
    {
        var desired = _dataNode.LocalItems;
        if (desired.Count != RootNodes.Count) return false;
        bool orderDiffers = false;
        for (int i = 0; i < desired.Count; i++)
        {
            if (!RootNodes.Contains(desired[i])) return false; // membership differs -> in-place is safe
            if (!ReferenceEquals(RootNodes[i], desired[i])) orderDiffers = true;
        }
        return orderDiffers;
    }

    public void ApplyFilter()
    {
        var source = _dataNode.LocalItems;

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // No search: mirror the real tree.
            MirrorViewChildren(source);
            SyncCollection(RootNodes, source);
        }
        else
        {
            // Search: prune to matches, keeping the hierarchy.
            var filtered = FilterRecursive(source, SearchText);
            SyncCollection(RootNodes, filtered);
        }
    }

    /// <summary>In-place add/remove/move so TreeView containers (and expansion state) survive,
    /// which a wholesale collection swap would destroy.</summary>
    private static void SyncCollection(ObservableCollection<BaseItem> target, IList<BaseItem> desired)
    {
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            int currentIndex = target.IndexOf(item);

            if (currentIndex < 0)
                target.Insert(i, item);
            else if (currentIndex != i)
                target.Move(currentIndex, i);
        }
    }

    /// <summary>Mirror Children into ViewChildren so the tree shows the full, unpruned hierarchy.</summary>
    private void MirrorViewChildren(IEnumerable<BaseItem> items)
    {
        foreach (var item in items)
        {
            SyncCollection(item.ViewChildren, item.Children);
            MirrorViewChildren(item.Children);
        }
    }

    /// <summary>Pruned tree for the query: an item survives if it matches or has a matching
    /// descendant; each survivor's ViewChildren holds only its matches. Returns the survivors.</summary>
    private List<BaseItem> FilterRecursive(IEnumerable<BaseItem> items, string query)
    {
        var matched = new List<BaseItem>();

        foreach (var item in items)
        {
            string searchPool = item.Title;

            if (item is Template t)
            {
                searchPool += $" {t.TagsCsv}";
                if (IncludeContentSearch)
                {
                    string plainContent = HtmlUtils.ToPlainText(t.Content);
                    searchPool += $" {plainContent}";
                }
            }

            bool isMatch = FuzzySearchEngine.IsMatch(query, searchPool, StrictSearch);

            var matchedChildren = FilterRecursive(item.Children, query);

            if (isMatch || matchedChildren.Count > 0)
            {
                SyncCollection(item.ViewChildren, matchedChildren);

                // Expand so matches are visible.
                if (item is Folder f)
                    f.IsExpanded = true;

                matched.Add(item);
            }
        }
        return matched;
    }

    #endregion

    #region CRUD Commands

    /// <summary>Target for a new item: the selected folder, a selected template's parent, else root.</summary>
    private Folder? AddTargetFolder() =>
        SelectedItem as Folder ?? (SelectedItem != null ? FindParent(AllItems, SelectedItem) as Folder : null);

    [RelayCommand]
    private async Task AddTemplate()
    {
        Folder? parent = AddTargetFolder();
        var siblings = parent?.Children ?? AllItems;

        var item = new Template
        {
            Title = GetUniqueTitle("New Template", siblings),
            Id = Guid.NewGuid(),
            Owner = this,
            DefaultPasteMode = _dataNode.CurrentSettings.DefaultPasteMode
        };

        if (parent != null) parent.IsExpanded = true;   // reveal the new item

        await _dataNode.AddItemAsync(item, parent);
        ApplyFilter();
        SelectedItem = item;
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        Folder? parent = AddTargetFolder();
        var siblings = parent?.Children ?? AllItems;

        var item = new Folder
        {
            Title = GetUniqueTitle("New Folder", siblings),
            Id = Guid.NewGuid()
        };

        if (parent != null) parent.IsExpanded = true;   // reveal the new item

        await _dataNode.AddItemAsync(item, parent);
        ApplyFilter();
        SelectedItem = item;
    }

    /// <summary>baseName, else the first free "baseName N" (fills gaps).</summary>
    private static string GetUniqueTitle(string baseName, IEnumerable<BaseItem> siblings)
    {
        var taken = new HashSet<string>(
            siblings.Select(s => s.Title ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName)) return baseName;

        int n = 1;
        while (taken.Contains($"{baseName} {n}")) n++;
        return $"{baseName} {n}";
    }

    [RelayCommand]
    private async Task CloneItem()
    {
        if (SelectedItem == null) return;

        // Read-only item → clone locally (outside the sync folder); else sibling of the original.
        Folder? parent = _dataNode.IsItemReadOnly(SelectedItem)
            ? null
            : FindParent(AllItems, SelectedItem) as Folder;
        var siblings = parent?.Children ?? AllItems;
        string cloneTitle = GetUniqueTitle($"{SelectedItem.Title} (Copy)", siblings);

        BaseItem clone = SelectedItem is Template t
            ? new Template
            {
                Title = cloneTitle,
                Content = t.Content,
                TagsCsv = t.TagsCsv,
                DefaultPasteMode = t.DefaultPasteMode,
                SingleKeyShortcut = t.SingleKeyShortcut,
                MultiKeyShortcut = t.MultiKeyShortcut,
                Owner = this
            }
            : new Folder { Title = cloneTitle };

        await _dataNode.AddItemAsync(clone, parent);

        ApplyFilter();
        SelectedItem = clone;
    }

    [RelayCommand]
    private async Task DeleteItem()
    {
        if (SelectedItem == null) return;
        await _dataNode.DeleteItemAsync(SelectedItem);
        ApplyFilter();
        SelectedItem = null;
    }

    public bool IsReadOnly(BaseItem item) => _dataNode.IsItemReadOnly(item);
    public string GetEffectiveMultiKey(Template t) => _dataNode.GetEffectiveMultiKey(t);

    public BaseItem? FindParent(IEnumerable<BaseItem> items, BaseItem child)
    {
        foreach (var item in items)
        {
            if (item.Children.Contains(child)) return item;
            var found = FindParent(item.Children, child);
            if (found != null) return found;
        }
        return null;
    }

    #endregion

    #region Backup/Storage

    [RelayCommand]
    private async Task SaveBackup()
    {
        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeChoices.Add("TextTemplateManager data", new List<string> { ".ttmdata" });
        picker.SuggestedFileName = $"Backup_{DateTime.Now:yyyyMMdd}";

        var file = await picker.PickSaveFileAsync();
        if (file != null)
            await _dataNode.ExportDataAsync(file.Path);
    }

    /// <summary>Exports a folder's subtree to a file the user picks (per-folder Export action).</summary>
    public async Task ExportFolderAsync(Folder folder)
    {
        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeChoices.Add("TextTemplateManager data", new List<string> { ".ttmdata" });
        picker.SuggestedFileName = folder.Title;

        var file = await picker.PickSaveFileAsync();
        if (file != null)
            await _dataNode.ExportFolderAsync(file.Path, folder);
    }

    [RelayCommand]
    private async Task LoadBackup()
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".ttmdata");

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        // Backups are a serialized root Folder — take its children (not a bare List<BaseItem>).
        var root = await StorageService.LoadRootAsync(file.Path);
        if (root == null) return;

        AllItems.Clear();
        foreach (var item in root.Children)
        {
            item.ParentId = Guid.Empty; // top-level → empty-GUID root
            AttachOwnerRecursive(item);
            AllItems.Add(item);
        }

        ValidateAllShortcuts();
        ApplyFilter();
    }

    private void AttachOwnerRecursive(BaseItem item)
    {
        if (item is Template t)
            t.Owner = this;

        foreach (var child in item.Children)
            AttachOwnerRecursive(child);
    }


    #endregion

    #region Drag & Drop Reordering

    // Suppress auto-save while WinUI mutates the shared collections mid-drag.
    public void BeginDrag() => _dataNode.BeginDrag();
    public void EndDrag() => _dataNode.EndDrag();

    private sealed record DragOrigin(SyncSource? Source, BaseItem? Parent, int Index);
    private readonly Dictionary<BaseItem, DragOrigin> _dragOrigins = new();

    /// <summary>Remember an item's origin (source + parent + index) so a bad drop can be reverted
    /// and a boundary crossing detected.</summary>
    public void CaptureDragOrigin(BaseItem item)
    {
        var source = _dataNode.GetSyncSourceForItem(item);
        var parent = FindParent(AllItems, item);
        var coll = (parent as Folder)?.Children ?? AllItems;
        _dragOrigins[item] = new DragOrigin(source, parent, coll.IndexOf(item));
    }

    public void ClearDragOrigins() => _dragOrigins.Clear();

    /// <summary>Enforce drop rules on the reconciled tree: templates can't be parents (a drop onto
    /// one becomes a sibling), read-only drops are reverted, and SyncIds are handed off across
    /// boundaries. Returns true if the tree was changed (so it must be rebuilt).</summary>
    private bool ApplyDropRules()
    {
        bool changed = false;
        foreach (var (item, origin) in _dragOrigins)
        {
            // Templates can never have children: a drop onto a template lands as a sibling below it.
            if (FindParent(AllItems, item) is Template target)
            {
                ReparentAsSibling(item, target);
                changed = true;
            }

            var dest = _dataNode.GetSyncSourceForItem(item);

            // Nothing may land in a read-only (save-off) sync folder — revert entirely.
            if (dest != null && !dest.AllowSave)
            {
                RevertToOrigin(item, origin);
                changed = true;
                continue;
            }

            // Crossed a sync-folder boundary: drop the old SyncId (write-through reassigns) and
            // de-conflict shortcuts against the new area.
            if (!ReferenceEquals(origin.Source, dest))
            {
                ClearSyncIdRecursive(item);
                EnforceAreaShortcutsOnMove(item);
            }
        }
        return changed;
    }

    /// <summary>Moves <paramref name="item"/> to sit directly below <paramref name="target"/> in the
    /// target's parent — used when something is dropped onto a (leaf) template.</summary>
    private void ReparentAsSibling(BaseItem item, Template target)
    {
        var siblings = (FindParent(AllItems, target) as Folder)?.Children ?? AllItems;
        RemoveFromTree(item);
        int idx = siblings.IndexOf(target);
        siblings.Insert(idx < 0 ? siblings.Count : idx + 1, item);
    }

    /// <summary>On a move into a new area, fix shortcut clashes with templates already there: a
    /// duplicate single key is cleared; a duplicate multi key gets a numeric suffix (MSG → MSG1).</summary>
    private void EnforceAreaShortcutsOnMove(BaseItem movedItem)
    {
        var movedTemplates = Flatten(new[] { movedItem }).OfType<Template>().ToList();
        if (movedTemplates.Count == 0) return;

        var movedSet = new HashSet<Template>(movedTemplates);
        Guid areaKey = _dataNode.GetAreaKey(movedItem);

        // Existing templates in the destination area (excluding the moved subtree).
        var areaTemplates = Flatten(AllItems).OfType<Template>()
            .Where(t => !movedSet.Contains(t) && _dataNode.GetAreaKey(t) == areaKey)
            .ToList();

        foreach (var t in movedTemplates)
        {
            if (!string.IsNullOrWhiteSpace(t.SingleKeyShortcut) &&
                areaTemplates.Any(o => string.Equals(o.SingleKeyShortcut, t.SingleKeyShortcut, StringComparison.OrdinalIgnoreCase)))
            {
                t.SingleKeyShortcut = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(t.MultiKeyShortcut))
            {
                var taken = new HashSet<string>(
                    areaTemplates.Where(o => !string.IsNullOrWhiteSpace(o.MultiKeyShortcut))
                                 .Select(o => o.MultiKeyShortcut),
                    StringComparer.OrdinalIgnoreCase);

                if (taken.Contains(t.MultiKeyShortcut))
                {
                    string baseKey = t.MultiKeyShortcut;
                    int n = 1;
                    string candidate = baseKey + n;
                    while (taken.Contains(candidate)) { n++; candidate = baseKey + n; }
                    t.MultiKeyShortcut = candidate;
                }
            }

            areaTemplates.Add(t);   // so later moved siblings avoid it too
        }
    }

    private void RevertToOrigin(BaseItem item, DragOrigin origin)
    {
        RemoveFromTree(item);
        var coll = (origin.Parent as Folder)?.Children ?? AllItems;
        int idx = Math.Clamp(origin.Index, 0, coll.Count);
        if (!coll.Contains(item)) coll.Insert(idx, item);
    }

    private void RemoveFromTree(BaseItem item)
    {
        if (AllItems.Remove(item)) return;
        foreach (var top in AllItems) if (RemoveFromChildren(top, item)) return;
    }

    private static bool RemoveFromChildren(BaseItem parent, BaseItem item)
    {
        if (parent.Children.Remove(item)) return true;
        foreach (var c in parent.Children) if (RemoveFromChildren(c, item)) return true;
        return false;
    }

    private static void ClearSyncIdRecursive(BaseItem item)
    {
        item.SyncId = null;
        foreach (var c in item.Children) ClearSyncIdRecursive(c);
    }

    /// <summary>Moves an item out to the root level (as a local item, after the pinned sync
    /// folders). A reliable alternative to dragging an item out of a folder.</summary>
    public void MoveToRoot(BaseItem item)
    {
        if (FindParent(AllItems, item) is null) return;   // already at root

        var origin = _dataNode.GetSyncSourceForItem(item);

        _dataNode.BeginDrag();   // suppress the transient auto-saves
        try
        {
            RemoveFromTree(item);
            AllItems.Add(item);            // append after the pinned sync folders / existing locals
            item.ParentId = Guid.Empty;

            if (origin != null)            // left a sync source -> now local
            {
                ClearSyncIdRecursive(item);
                EnforceAreaShortcutsOnMove(item);
            }
        }
        finally { _dataNode.EndDrag(); }

        ReloadTree();
        _ = SaveCurrentStateAsync();
    }

    public Task SyncMasterAfterDragAsync()
    {
        DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
        {
            try
            {
                // Skip while searching: the pruned view isn't the real order, so the drag is
                // discarded when search clears and ViewChildren rebuilds from Children.
                if (!string.IsNullOrWhiteSpace(SearchText)) return;

                // The drag mutated RootNodes/ViewChildren, not the real data — rebuild from them.
                var currentTree = RootNodes.ToList();
                AllItems.Clear();
                foreach (var item in currentTree)
                    AllItems.Add(item);

                foreach (var item in AllItems)
                    ReconcileChildrenFromView(item);

                NormalizeParentIds(AllItems, Guid.Empty);

                // A rule (template-sibling, revert, ...) changed the data — rebuild the tree so the
                // visual result matches (TreeView doesn't reflect in-place moves reliably).
                if (ApplyDropRules()) ReloadTree();
                ClearDragOrigins();
            }
            finally
            {
                _dataNode.EndDrag();   // re-enable saving before the final save
            }

            await SaveCurrentStateAsync();   // single authoritative save for the drag
        });

        return Task.CompletedTask;
    }

    /// <summary>Set each item's ParentId to its actual container, so the stored field doesn't go
    /// stale after a move.</summary>
    private void NormalizeParentIds(IEnumerable<BaseItem> items, Guid parentId)
    {
        foreach (var item in items)
        {
            item.ParentId = parentId;

            if (item is Folder folder)
                NormalizeParentIds(folder.Children, folder.Id);
        }
    }

    /// <summary>Rebuild real Children from ViewChildren (what the drag mutated). Reconciles ALL
    /// items, not just folders: a drag can drop an item into a template's ViewChildren, and this
    /// captures it into Children so ApplyDropRules can re-parent it as a sibling.</summary>
    private void ReconcileChildrenFromView(BaseItem item)
    {
        var ordered = item.ViewChildren.ToList();
        item.Children.Clear();
        foreach (var child in ordered)
            item.Children.Add(child);

        foreach (var child in item.ViewChildren)
            ReconcileChildrenFromView(child);
    }

    public async Task SaveCurrentStateAsync()
    {
        if (AllItems
            .SelectManyRecursive(i => i.Children)
            .OfType<Template>()
            .Any(t => t.HasSingleKeyConflict || t.HasMultiKeyConflict))
        {
            return;
        }

        await _dataNode.SaveDataAsync();
    }

    #endregion

    partial void OnSelectedItemChanged(BaseItem? value)
    {
        // Re-raise so MainPage re-syncs the editor to the new selection.
        OnPropertyChanged(nameof(SelectedItem));
        ValidateAllShortcuts();
    }

    private IEnumerable<BaseItem> Flatten(IEnumerable<BaseItem> source)
    {
        foreach (var item in source)
        {
            yield return item;

            foreach (var child in Flatten(item.Children))
                yield return child;
        }
    }

    public void ValidateAllShortcuts()
    {
        var templates = Flatten(AllItems)
            .OfType<Template>()
            .ToList();

        foreach (var t in templates)
        {
            t.HasSingleKeyConflict = false;
            t.HasMultiKeyConflict = false;
            t.HasSingleKeyCrossAreaWarning = false;
        }

        // Single-key conflicts are per-area; the same key in different areas is allowed.
        var singleGroups = templates
            .Where(t => !string.IsNullOrWhiteSpace(t.SingleKeyShortcut))
            .GroupBy(t => (Area: _dataNode.GetAreaKey(t), Key: t.SingleKeyShortcut.Trim().ToUpperInvariant()))
            .Where(g => g.Count() > 1);

        foreach (var g in singleGroups)
            foreach (var t in g)
                t.HasSingleKeyConflict = true;

        // Same key across areas: allowed, flagged as a warning only.
        var crossAreaGroups = templates
            .Where(t => !string.IsNullOrWhiteSpace(t.SingleKeyShortcut))
            .GroupBy(t => t.SingleKeyShortcut.Trim().ToUpperInvariant())
            .Where(g => g.Select(t => _dataNode.GetAreaKey(t)).Distinct().Count() > 1);

        foreach (var g in crossAreaGroups)
            foreach (var t in g)
                if (!t.HasSingleKeyConflict)
                    t.HasSingleKeyCrossAreaWarning = true;

        // Multi-key compared on the effective (prefixed) shortcut, so it's area-scoped.
        var multiGroups = templates
            .Where(t => !string.IsNullOrWhiteSpace(t.MultiKeyShortcut))
            .GroupBy(t => _dataNode.GetEffectiveMultiKey(t).Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var g in multiGroups)
            foreach (var t in g)
                t.HasMultiKeyConflict = true;
    }



}