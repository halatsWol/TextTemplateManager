using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using TextTemplateManager.Common;
using TextTemplateManager.Data;
using TextTemplateManager.Helpers;
using TextTemplateManager.Models;
using TextTemplateManager.Services.Pasting;

namespace TextTemplateManager.Services.System;

/// <summary>Serves the connector from the live template tree. The tree is snapshotted on the UI
/// thread (<see cref="Rebuild"/>) into an immutable structure the connector's background threads
/// read; rendering is a pure function so it runs off-thread safely.</summary>
public sealed class AppConnectorData : IConnectorDataSource
{
    private sealed record Snap(List<ConnectorNodeDto> Tree, Dictionary<string, TemplateEntry> Templates);
    private sealed record TemplateEntry(string Name, string Content, PasteMode Default);

    private volatile Snap _snap = new(new(), new());

    // Captured on the UI thread (this is constructed there); writes marshal back to it.
    private readonly DispatcherQueue? _ui = DispatcherQueue.GetForCurrentThread();

    public string AppVersion => VersionString();

    public IReadOnlyList<PasteModeDto> PasteModes() =>
        PasteModeLabel.DisplayOrder.Select(m => new PasteModeDto(m.ToString(), PasteModeLabel.For(m))).ToList();

    public IReadOnlyList<ConnectorNodeDto> Tree() => _snap.Tree;

    public TemplateContentDto? Template(string id, string? mode)
    {
        if (!_snap.Templates.TryGetValue(id, out var t)) return null;
        PasteMode m = ParseMode(mode, t.Default);
        var (content, contentType) = PasteService.RenderForMode(t.Content, m);
        return new TemplateContentDto(id, t.Name, m.ToString(), contentType, content);
    }

    /// <summary>Creates a template in the local area with the given content and returns its id/name.
    /// Runs on the UI thread (tree edit + save); the calling connector thread blocks until it's done.</summary>
    public CreatedTemplateDto CreateTemplate(string content, string? name)
    {
        var ui = _ui ?? throw new InvalidOperationException("connector has no UI dispatcher");
        var tcs = new TaskCompletionSource<CreatedTemplateDto>();

        bool queued = ui.TryEnqueue(async () =>
        {
            try
            {
                string title = UniqueTitle(CleanName(name), DataNode.Instance.LocalItems);
                var item = new Template
                {
                    Id = Guid.NewGuid(),
                    Title = title,
                    // Content is untrusted HTML (a browser selection) — strip active content so a
                    // stored template can't carry script into paste targets or connector replies.
                    Content = HtmlSanitizer.Sanitize(content),
                    DefaultPasteMode = DataNode.Instance.CurrentSettings.DefaultPasteMode,
                };
                await DataNode.Instance.AddItemAsync(item);   // parent null -> local root
                Rebuild();                                     // include it in the served snapshot
                DataNode.Instance.NotifyTreeChanged();         // refresh the main-window tree projection
                tcs.TrySetResult(new CreatedTemplateDto(item.Id.ToString(), title));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        if (!queued) throw new InvalidOperationException("UI thread unavailable");

        // Block the connector thread until the UI thread finishes — bounded so a stalled/closing UI
        // can't hang the connection forever.
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("create-template timed out");
        return tcs.Task.GetAwaiter().GetResult();
    }

    // A plain-text title from the (untrusted) name field: no control chars, trimmed, length-capped.
    private static string CleanName(string? name)
    {
        string s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0) return "New Template";
        return s.Length > 120 ? s[..120].Trim() : s;
    }

    // baseName, else the first free "baseName N" — mirrors the app's new-item naming.
    private static string UniqueTitle(string baseName, IEnumerable<BaseItem> siblings)
    {
        var taken = new HashSet<string>(siblings.Select(s => s.Title ?? ""), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        int n = 1;
        while (taken.Contains($"{baseName} {n}")) n++;
        return $"{baseName} {n}";
    }

    /// <summary>Rebuilds the snapshot from the live tree. MUST be called on the UI thread.</summary>
    public void Rebuild()
    {
        var templates = new Dictionary<string, TemplateEntry>();
        var tree = BuildNodes(DataNode.Instance.LocalItems, templates);
        _snap = new Snap(tree, templates);
    }

    private static List<ConnectorNodeDto> BuildNodes(IEnumerable<BaseItem> items, Dictionary<string, TemplateEntry> templates)
    {
        var list = new List<ConnectorNodeDto>();
        foreach (var item in items)
        {
            string id = item.Id.ToString();
            string source = DataNode.Instance.GetSyncSourceForItem(item)?.Name ?? "local";
            if (item is Template t)
            {
                templates[id] = new TemplateEntry(t.Title, t.Content, t.DefaultPasteMode);
                list.Add(new ConnectorNodeDto(id, t.Title, "template", t.DefaultPasteMode.ToString(), source, null));
            }
            else if (item is Folder f)
            {
                list.Add(new ConnectorNodeDto(id, f.Title, "folder", null, source, BuildNodes(f.Children, templates)));
            }
        }
        return list;
    }

    private static PasteMode ParseMode(string? mode, PasteMode fallback)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode.Equals("default", StringComparison.OrdinalIgnoreCase))
            return fallback;
        return Enum.TryParse<PasteMode>(mode, ignoreCase: true, out var m) && Enum.IsDefined(m) ? m : fallback;
    }

    private static string VersionString()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        var v = asm.GetName().Version ?? new Version(0, 0, 0);
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
