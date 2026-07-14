using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TextTemplateManager.Common;

namespace TextTemplateManager.Models
{
    /// <summary>Merges a loaded sync file into the app-side folder. Items are matched by
    /// <see cref="BaseItem.SyncId"/> and reused in place, so tree identity (expansion/selection)
    /// survives. With <c>allowSave</c>, the newer LastChange wins; otherwise the file wins.</summary>
    public static class SyncEngine
    {
        public static void Merge(Folder target, Folder fileRoot, string name, Guid sourceId, bool allowSave)
        {
            var cache = new Dictionary<Guid, BaseItem>();
            IndexBySyncId(target.Children, cache);

            var merged = fileRoot.Children.Select(fc => BuildMerged(fc, cache, allowSave)).ToList();

            target.Id = sourceId;
            target.SyncId = fileRoot.Id;   // the sync file's root id (usually Guid.Empty)
            target.Title = name;
            target.ItemType = ItemType.Folder;
            // Adopt the file's root timestamp. Without this, each device keeps its own cached value
            // and writes it back on load, so a read-only open re-serializes differently and churns
            // the shared file — which makes OneDrive spawn conflict copies across devices.
            target.LastChange = fileRoot.LastChange;

            SyncChildren(target.Children, merged);
        }

        private static void IndexBySyncId(IEnumerable<BaseItem> items, Dictionary<Guid, BaseItem> map)
        {
            foreach (var it in items)
            {
                if (it.SyncId is Guid sid) map[sid] = it;
                IndexBySyncId(it.Children, map);
            }
        }

        private static BaseItem BuildMerged(BaseItem fileItem, Dictionary<Guid, BaseItem> cache, bool allowSave)
        {
            cache.TryGetValue(fileItem.Id, out var cached);

            // The cached (app) copy wins only if saving is allowed AND it is strictly newer.
            bool cacheWins = allowSave && cached != null &&
                string.CompareOrdinal(cached.LastChange ?? "", fileItem.LastChange ?? "") > 0;

            BaseItem result;
            if (cached != null)
            {
                // Reuse the existing object so its tree identity (expansion/selection) is preserved.
                result = cached;
                if (!cacheWins) CopyContent(fileItem, result); // adopt the file's newer content
            }
            else
            {
                // New item introduced by the file.
                result = fileItem is Template ? new Template() : new Folder();
                result.Id = Guid.NewGuid();
                CopyContent(fileItem, result);
            }

            result.SyncId = fileItem.Id;

            var mergedChildren = fileItem.Children.Select(fc => BuildMerged(fc, cache, allowSave)).ToList();
            SyncChildren(result.Children, mergedChildren);

            return result;
        }

        private static void CopyContent(BaseItem from, BaseItem to)
        {
            to.Title = from.Title;
            to.LastChange = from.LastChange;
            if (from is Template ft && to is Template tt)
            {
                tt.Content = ft.Content;
                tt.SingleKeyShortcut = ft.SingleKeyShortcut;
                tt.MultiKeyShortcut = ft.MultiKeyShortcut;
                tt.TagsCsv = ft.TagsCsv;
                tt.DefaultPasteMode = ft.DefaultPasteMode;
            }
        }

        /// <summary>Updates <paramref name="target"/> in place to match <paramref name="desired"/>
        /// (add/remove/move), preserving the reused item objects and their containers.</summary>
        private static void SyncChildren(ObservableCollection<BaseItem> target, List<BaseItem> desired)
        {
            for (int i = target.Count - 1; i >= 0; i--)
                if (!desired.Contains(target[i])) target.RemoveAt(i);

            for (int i = 0; i < desired.Count; i++)
            {
                int cur = target.IndexOf(desired[i]);
                if (cur < 0) target.Insert(i, desired[i]);
                else if (cur != i) target.Move(cur, i);
            }
        }
    }
}
