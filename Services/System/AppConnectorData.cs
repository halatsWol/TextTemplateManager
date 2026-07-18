using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
