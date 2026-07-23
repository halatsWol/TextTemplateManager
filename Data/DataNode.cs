using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TextTemplateManager.Models;

namespace TextTemplateManager.Data;

public class DataNode
{
    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);
    private readonly System.Threading.SemaphoreSlim _syncApplyLock = new(1, 1);
    private readonly string _localDataPath;
    private bool _isInitialized = false;
    private bool _isMoving = false;

    private static DataNode? _instance;
    public static DataNode Instance => _instance ??= new DataNode();

    public IEnumerable<Template> AllItems => GetAllTemplates(RootFolder);

    private IEnumerable<Template> GetAllTemplates(Folder folder)
    {
        var list = new List<Template>();
        foreach (var item in folder.Children)
        {
            if (item is Template template) list.Add(template);
            else if (item is Folder subFolder) list.AddRange(GetAllTemplates(subFolder));
        }
        return list;
    }

    // Guid.Empty = root level.
    private Folder RootFolder { get; set; } = new Folder
    {
        Id = Guid.Empty,
        Title = "Root"
    };

    public ObservableCollection<BaseItem> LocalItems => RootFolder.Children;
    public AppSettings CurrentSettings { get; private set; } = new();
    public SyncSettings CurrentSyncSettings { get; private set; } = new();

    /// <summary>Raised (after a sync re-apply) so the UI can refresh the tree projection.</summary>
    public event Action? TreeChanged;

    /// <summary>Raise TreeChanged for a structural change made outside the ViewModel (e.g. a template
    /// created via the browser connector), so the main-window tree re-projects.</summary>
    public void NotifyTreeChanged() => TreeChanged?.Invoke();

    /// <summary>Raised after template data is successfully written to disk (may be off the UI thread).</summary>
    public event Action? DataSaved;

    /// <summary>Last observed write time per source, so the poller ignores our own writes.</summary>
    private readonly Dictionary<Guid, DateTime> _lastSeenWrite = new();

    public DataNode()
    {
        _localDataPath = StorageService.GetDataPath();
    }

    #region Initialization


    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        var loadedRoot = await StorageService.LoadRootAsync(_localDataPath);
        if (loadedRoot != null)
        {
            _isMoving = true;   // no save-loops during load
            try
            {
                // Keep persisted root metadata so an unchanged tree re-saves identically.
                RootFolder.Id = loadedRoot.Id;
                RootFolder.LastChange = loadedRoot.LastChange;

                RootFolder.Children.Clear();
                foreach (var item in loadedRoot.Children)
                {
                    RootFolder.Children.Add(item);
                    AttachChangeTracking(item);
                }
            }
            finally { _isMoving = false; }
        }

        // First run: seed RunAtStartup from the OS autostart entry rather than defaulting it on.
        // Thereafter the settings file wins and the OS entry is reconciled to it.
        bool settingsExisted = File.Exists(StorageService.GetSettingsPath());
        CurrentSettings = await StorageService.LoadSettingsAsync();
        if (!settingsExisted)
        {
            CurrentSettings.RunAtStartup = Services.System.StartupManager.IsEnabled();
            await StorageService.SaveSettingsAsync(CurrentSettings);
        }
        Services.System.StartupManager.SetEnabled(CurrentSettings.RunAtStartup);

        CurrentSyncSettings = await StorageService.LoadSyncSettingsAsync();
        // Valid separators are '-', '.', or "" (none); migrate any legacy value (e.g. '_') so
        // multi-key shortcuts resolve consistently even before Sync settings is opened.
        if (CurrentSyncSettings.Separator is not ("." or "-" or ""))
        {
            CurrentSyncSettings.Separator = "-";
            await SaveSyncSettingsAsync();
        }

        // Merge active sources; guard the churn from autosaving.
        _isMoving = true;
        try { await ApplySyncAsync(); }
        finally { _isMoving = false; }
        foreach (var item in RootFolder.Children) AttachChangeTracking(item);

        _isInitialized = true;

        // Persist merged tree + push newer local changes to the sync files.
        if (CurrentSyncSettings.Sources.Any(s => s.IsActive))
            await SaveDataAsync();
    }

    public Task SaveSyncSettingsAsync() => StorageService.SaveSyncSettingsAsync(CurrentSyncSettings);

    #endregion

    #region Tree Operations (Move, Add, Delete)

    public async Task AddItemAsync(BaseItem item, Folder? parent = null)
    {
        var target = parent ?? RootFolder;
        item.ParentId = target.Id;
        target.Children.Add(item);
        AttachChangeTracking(item);
        await SaveDataAsync();
    }

    public async Task DeleteItemAsync(BaseItem item)
    {
        if (item == null) return;
        RemoveFromTreeById(RootFolder.Children, item.Id);
        await SaveDataAsync();
    }

    public async Task ExportDataAsync(string path)
    {
        await StorageService.SaveAsync(path, RootFolder);
    }

    /// <summary>Export a folder's subtree to a standalone file (the folder becomes the root).</summary>
    public async Task ExportFolderAsync(string path, Folder folder)
    {
        var root = new Folder { Id = folder.Id, Title = folder.Title, LastChange = folder.LastChange };
        foreach (var child in folder.Children) root.Children.Add(child);
        await StorageService.SaveAsync(path, root);
    }

    public async Task MoveItemAsync(BaseItem item, Folder? targetFolder)
    {
        if (item == null) return;

        _isMoving = true;
        try
        {
            var destination = targetFolder ?? RootFolder;

            RemoveFromTreeById(RootFolder.Children, item.Id);   // no duplicates
            item.ParentId = destination.Id;
            item.LastChange = DateTime.Now.ToString("yyyyMMddHHmmss");

            if (!destination.Children.Contains(item))
                destination.Children.Insert(0, item);

            AttachChangeTracking(item);
        }
        finally
        {
            _isMoving = false;
            await SaveDataAsync();
        }
    }
    private void RemoveFromTreeById(ObservableCollection<BaseItem> collection, Guid id)
    {
        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (collection[i].Id == id)
            {
                collection.RemoveAt(i);
                return;
            }
            if (collection[i] is Folder folder)
            {
                RemoveFromTreeById(folder.Children, id);
            }
        }
    }

    #endregion

    #region Tracking & Persistence

    /// <summary>Suppress auto-save during a drag; the drag's final save is authoritative.</summary>
    public void BeginDrag() => _isMoving = true;

    /// <summary>Re-enable auto-save. Call BEFORE the final save so it isn't suppressed.</summary>
    public void EndDrag() => _isMoving = false;

    public void AttachChangeTracking(BaseItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
        item.PropertyChanged += OnItemPropertyChanged;
        item.Children.CollectionChanged -= OnChildrenCollectionChanged;
        item.Children.CollectionChanged += OnChildrenCollectionChanged;

        foreach (var child in item.Children)
        {
            AttachChangeTracking(child);
        }
    }

    // Transient/derived properties (expansion, list display projections, conflict flags) are not
    // real data edits: changing them must not bump LastChange or save, which would churn the sync
    // file — e.g. Quick Paste stamps EffectiveMultiKey/SourceLabel on the tree templates when it opens.
    private static readonly HashSet<string> _nonPersistedProps = new()
    {
        nameof(BaseItem.IsExpanded),
        nameof(Template.EffectiveMultiKey),
        nameof(Template.SourceLabel),
        nameof(Template.IsLocalSource),
        nameof(Template.HasSingleKeyConflict),
        nameof(Template.HasMultiKeyConflict),
        nameof(Template.HasSingleKeyCrossAreaWarning),
        nameof(Template.HasMultiKeyCrossAreaWarning),
    };

    private CancellationTokenSource? _editSaveDebounce;

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isMoving || e.PropertyName == null || _nonPersistedProps.Contains(e.PropertyName)) return;
        if (sender is BaseItem item)
        {
            item.LastChange = DateTime.Now.ToString("yyyyMMddHHmmss");
            ScheduleEditSave();
        }
    }

    // Debounce the disk write for property edits: typing a title/tags/shortcut (and the editor's own
    // debounced content changes) coalesce into one save a short moment after the last edit, instead of
    // writing on every keystroke. Structural changes (add/delete/move) still call SaveDataAsync directly.
    private void ScheduleEditSave()
    {
        _editSaveDebounce?.Cancel();
        var cts = _editSaveDebounce = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(cts.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken ct)
    {
        // No token on Task.Delay on purpose: passing it would throw TaskCanceledException on every
        // superseding keystroke (caught, but a noisy first-chance exception while typing). Instead the
        // delay always completes and we drop the save here if a newer edit has replaced it.
        await Task.Delay(500);
        if (!ct.IsCancellationRequested) await SaveDataAsync();
    }

    private async void OnChildrenCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_isMoving) return;
        if (e.NewItems != null)
        {
            foreach (BaseItem newItem in e.NewItems) AttachChangeTracking(newItem);
        }
        await SaveDataAsync();
    }

    public async Task SaveDataAsync()
    {
        if (_isMoving || !_isInitialized) return;
        await _saveLock.WaitAsync();
        bool saved = false;
        try
        {
            await StorageService.SaveAsync(_localDataPath, RootFolder);
            await WriteSyncSourcesAsync();
            saved = true;
        }
        finally
        {
            _saveLock.Release();
        }
        if (saved) DataSaved?.Invoke();
    }

    #endregion

    #region Sync

    /// <summary>Re-runs the sync reconcile at runtime (after settings change or an external file
    /// change), refreshes the tree via <see cref="TreeChanged"/>, and persists.</summary>
    public async Task ReapplySyncAsync()
    {
        if (!_isInitialized) return;

        // Serialize re-applies. A manual reorder, an add/remove, and the 4s external-change poll can
        // otherwise overlap and run two ApplySyncAsync at once, mutating RootFolder.Children (Clear +
        // re-add) concurrently across their file-read awaits — a garbled tree or a crash. Callers
        // await, so a re-apply that arrives mid-flight still runs once the current one finishes.
        await _syncApplyLock.WaitAsync();
        try
        {
            _isMoving = true;
            try { await ApplySyncAsync(); }
            finally { _isMoving = false; }

            foreach (var item in RootFolder.Children) AttachChangeTracking(item);
            TreeChanged?.Invoke();
            await SaveDataAsync();
        }
        finally { _syncApplyLock.Release(); }
    }

    /// <summary>Polls the active sources' files and re-applies sync if any changed EXTERNALLY.
    /// Cheap when nothing changed (just reads each file's timestamp; never opens/locks them).</summary>
    public async Task CheckSyncFilesForChangesAsync()
    {
        if (!_isInitialized) return;

        bool changed = false;
        foreach (var source in CurrentSyncSettings.Sources.Where(s => s.IsActive).ToList())
        {
            DateTime write = GetLastWriteSafe(source.Path);
            if (_lastSeenWrite.TryGetValue(source.Id, out var prev) && write != prev)
                changed = true;
            _lastSeenWrite[source.Id] = write;
        }

        if (changed) await ReapplySyncAsync();
    }

    private static DateTime GetLastWriteSafe(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch { return default; }
    }

    /// <summary>Returns the sync source an item belongs to (via its top-level ancestor), or null
    /// if the item is local.</summary>
    public SyncSource? GetSyncSourceForItem(BaseItem item)
    {
        var top = FindTopLevelAncestor(item);
        return top == null ? null : CurrentSyncSettings.Sources.FirstOrDefault(s => s.Id == top.Id);
    }

    public bool IsItemReadOnly(BaseItem item)
    {
        var src = GetSyncSourceForItem(item);
        return src != null && !src.AllowSave;
    }

    /// <summary>Area identity: the sync source's Id, or Guid.Empty for a local item.</summary>
    public Guid GetAreaKey(BaseItem item) => GetSyncSourceForItem(item)?.Id ?? Guid.Empty;

    /// <summary>Area priority: 0 = local (wins), then 1..N by active-source order. Resolves which
    /// template wins when a single key repeats across areas.</summary>
    public int GetSourcePriority(BaseItem item)
    {
        var src = GetSyncSourceForItem(item);
        if (src == null) return 0; // local always wins
        var active = CurrentSyncSettings.Sources.Where(s => s.IsActive).ToList();
        int idx = active.IndexOf(src);
        return idx < 0 ? int.MaxValue : idx + 1;
    }

    /// <summary>Highest-priority template for a single key (local first, then sync order).</summary>
    public Template? ResolveSingleKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return AllItems
            .Where(t => string.Equals(t.SingleKeyShortcut, key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(GetSourcePriority)
            .FirstOrDefault();
    }

    /// <summary>The multikey as it must be TYPED: prefix-namespaced in a sync folder (and-msg),
    /// else the raw shortcut. The template still stores just "msg".</summary>
    public string GetEffectiveMultiKey(Template t)
    {
        if (t == null || string.IsNullOrEmpty(t.MultiKeyShortcut)) return t?.MultiKeyShortcut ?? string.Empty;
        var src = GetSyncSourceForItem(t);
        return src != null && !string.IsNullOrWhiteSpace(src.ShortcutPrefix)
            ? src.ShortcutPrefix + (CurrentSyncSettings.Separator ?? "-") + t.MultiKeyShortcut
            : t.MultiKeyShortcut;
    }

    private BaseItem? FindTopLevelAncestor(BaseItem item)
    {
        foreach (var top in RootFolder.Children)
            if (ReferenceEquals(top, item) || ContainsRecursive(top, item)) return top;
        return null;
    }

    private static bool ContainsRecursive(BaseItem parent, BaseItem target)
    {
        foreach (var c in parent.Children)
            if (ReferenceEquals(c, target) || ContainsRecursive(c, target)) return true;
        return false;
    }

    /// <summary>Reconcile the tree with the sync sources: a pinned folder per active source
    /// (file merged with cache), inactive/removed dropped, synced folders first then local.</summary>
    private async Task ApplySyncAsync()
    {
        var sources = CurrentSyncSettings.Sources;

        // Split existing top-level children: a synced folder root is a Folder with a SyncId set.
        var cachedSynced = new Dictionary<Guid, Folder>();
        var localItems = new List<BaseItem>();
        foreach (var child in RootFolder.Children)
        {
            if (child is Folder f && child.SyncId != null) cachedSynced[child.Id] = f;
            else localItems.Add(child);
        }

        var syncedFolders = new List<Folder>();
        // Snapshot: the loop awaits a file read per source, during which the collection may change.
        foreach (var source in sources.Where(s => s.IsActive).ToList())
        {
            cachedSynced.TryGetValue(source.Id, out var folder);
            folder ??= new Folder { Id = source.Id };
            folder.IsSyncRoot = true;

            Folder? fileRoot = null;
            if (!string.IsNullOrWhiteSpace(source.Path) && File.Exists(source.Path))
            {
                try { fileRoot = await StorageService.LoadRootSharedAsync(source.Path); }
                catch { fileRoot = null; }
                _lastSeenWrite[source.Id] = GetLastWriteSafe(source.Path); // ignore our own reads later
            }

            if (fileRoot != null)
            {
                SyncEngine.Merge(folder, fileRoot, source.Name, source.Id, source.AllowSave);
                folder.SyncFileMissing = false;
            }
            else
            {
                // Missing/unreadable file: keep the cached content, flag it, use the settings name.
                folder.Title = source.Name;
                folder.SyncId ??= Guid.Empty;
                folder.SyncFileMissing = true;
            }

            syncedFolders.Add(folder);
        }

        // Synced folders first (settings order), then local. Dropped sources aren't re-added.
        RootFolder.Children.Clear();
        foreach (var f in syncedFolders) RootFolder.Children.Add(f);
        foreach (var it in localItems) RootFolder.Children.Add(it);
    }

    /// <summary>Writes each active, save-enabled source's folder back to its file (skips missing
    /// files). StorageService skips the write when the content is unchanged.</summary>
    private async Task WriteSyncSourcesAsync()
    {
        // Snapshot: the loop awaits file writes, during which another flow (opening a .ttmdata, a
        // Sync-settings edit, a save/poll timer) may add or remove a source and invalidate the iterator.
        foreach (var source in CurrentSyncSettings.Sources.Where(s => s.IsActive && s.AllowSave).ToList())
        {
            if (RootFolder.Children.FirstOrDefault(c => c.Id == source.Id) is not Folder folder) continue;
            if (folder.SyncFileMissing) continue;
            try
            {
                await StorageService.SaveSharedAsync(source.Path, BuildFileTree(folder));
                _lastSeenWrite[source.Id] = GetLastWriteSafe(source.Path); // don't treat our write as external
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Convert a synced folder's subtree into a sync-file tree keyed by SyncId
    /// (locally-added items get a fresh SyncId here).</summary>
    private static Folder BuildFileTree(Folder syncFolder)
    {
        var root = new Folder
        {
            Id = syncFolder.SyncId ?? Guid.Empty,
            // Fixed root title so the shared file doesn't churn with each PC's local name.
            Title = "Root",
            LastChange = syncFolder.LastChange,
        };
        foreach (var child in syncFolder.Children)
            root.Children.Add(ToFileItem(child, root.Id));
        return root;
    }

    private static BaseItem ToFileItem(BaseItem appItem, Guid parentSyncId)
    {
        appItem.SyncId ??= Guid.NewGuid();   // assign a file id to locally-added items
        Guid fileId = appItem.SyncId.Value;

        BaseItem fileItem = appItem is Template t
            ? new Template
            {
                Title = t.Title,
                Content = t.Content,
                SingleKeyShortcut = t.SingleKeyShortcut,
                MultiKeyShortcut = t.MultiKeyShortcut,
                TagsCsv = t.TagsCsv,
                DefaultPasteMode = t.DefaultPasteMode,
            }
            : new Folder { Title = appItem.Title };

        fileItem.Id = fileId;
        fileItem.ParentId = parentSyncId;
        fileItem.LastChange = appItem.LastChange;
        foreach (var c in appItem.Children)
            fileItem.Children.Add(ToFileItem(c, fileId));
        return fileItem;
    }

    #endregion
}