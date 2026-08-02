using TextTemplateManager.Common;
using TextTemplateManager.Models;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Sync merge rules. This is where cross-device data loss would come from: the merge decides
/// whether the local copy or the shared file wins, and it mutates the live tree in place rather than
/// rebuilding it, so object identity has to be preserved for expansion/selection to survive a refresh.</summary>
public class SyncEngineTests
{
    private const string Older = "20260101120000";
    private const string Newer = "20260601120000";

    private static Template FileTemplate(Guid syncId, string title, string content, string lastChange) =>
        new() { Id = syncId, Title = title, Content = content, LastChange = lastChange };

    private static Folder FileRoot(params BaseItem[] children)
    {
        var root = new Folder { Id = Guid.Empty, Title = "Root", LastChange = Older };
        foreach (var c in children) root.Children.Add(c);
        return root;
    }

    // ---- adopting the file ----

    [Fact]
    public void Items_only_in_the_file_are_added()
    {
        var target = new Folder();
        var sid = Guid.NewGuid();

        SyncEngine.Merge(target, FileRoot(FileTemplate(sid, "Alpha", "<p>A</p>", Older)),
                         "My Sync", Guid.NewGuid(), allowSave: true);

        var added = Assert.Single(target.Children);
        Assert.Equal("Alpha", added.Title);
        Assert.Equal(sid, added.SyncId);
    }

    [Fact]
    public void Merge_stamps_the_target_with_the_source_identity_and_name()
    {
        var target = new Folder();
        var sourceId = Guid.NewGuid();

        SyncEngine.Merge(target, FileRoot(), "My Sync", sourceId, allowSave: true);

        Assert.Equal(sourceId, target.Id);
        Assert.Equal(Guid.Empty, target.SyncId);      // the file root's own id
        Assert.Equal("My Sync", target.Title);
        Assert.Equal(ItemType.Folder, target.ItemType);
    }

    [Fact]
    public void Target_adopts_the_file_root_timestamp()
    {
        // Without this each device keeps its own cached value and writes it back on load, so even a
        // read-only open re-serialises differently — which is what spawns OneDrive conflict copies.
        var target = new Folder { LastChange = Newer };
        var fileRoot = FileRoot();
        fileRoot.LastChange = Older;

        SyncEngine.Merge(target, fileRoot, "My Sync", Guid.NewGuid(), allowSave: true);

        Assert.Equal(Older, target.LastChange);
    }

    [Fact]
    public void Items_missing_from_the_file_are_removed()
    {
        var keep = Guid.NewGuid();
        var target = new Folder();
        target.Children.Add(new Template { SyncId = keep, Title = "Alpha", LastChange = Older });
        target.Children.Add(new Template { SyncId = Guid.NewGuid(), Title = "Deleted elsewhere", LastChange = Older });

        SyncEngine.Merge(target, FileRoot(FileTemplate(keep, "Alpha", "<p>A</p>", Older)),
                         "My Sync", Guid.NewGuid(), allowSave: true);

        Assert.Equal(new[] { "Alpha" }, target.Children.Select(c => c.Title));
    }

    // ---- who wins ----

    [Fact]
    public void A_newer_local_edit_survives_when_saving_is_allowed()
    {
        var sid = Guid.NewGuid();
        var target = new Folder();
        target.Children.Add(new Template { SyncId = sid, Title = "Local title", Content = "<p>local</p>", LastChange = Newer });

        SyncEngine.Merge(target, FileRoot(FileTemplate(sid, "File title", "<p>file</p>", Older)),
                         "My Sync", Guid.NewGuid(), allowSave: true);

        var item = Assert.IsType<Template>(target.Children[0]);
        Assert.Equal("Local title", item.Title);
        Assert.Equal("<p>local</p>", item.Content);
    }

    [Fact]
    public void A_newer_file_edit_overwrites_the_local_copy()
    {
        var sid = Guid.NewGuid();
        var target = new Folder();
        target.Children.Add(new Template { SyncId = sid, Title = "Local title", Content = "<p>local</p>", LastChange = Older });

        SyncEngine.Merge(target, FileRoot(FileTemplate(sid, "File title", "<p>file</p>", Newer)),
                         "My Sync", Guid.NewGuid(), allowSave: true);

        var item = Assert.IsType<Template>(target.Children[0]);
        Assert.Equal("File title", item.Title);
        Assert.Equal("<p>file</p>", item.Content);
    }

    [Fact]
    public void Equal_timestamps_let_the_file_win()
    {
        // cacheWins requires strictly newer, so a tie resolves towards the shared file — the tiebreak
        // that keeps devices converging instead of ping-ponging.
        var sid = Guid.NewGuid();
        var target = new Folder();
        target.Children.Add(new Template { SyncId = sid, Title = "Local title", LastChange = Older });

        SyncEngine.Merge(target, FileRoot(FileTemplate(sid, "File title", "<p>f</p>", Older)),
                         "My Sync", Guid.NewGuid(), allowSave: true);

        Assert.Equal("File title", target.Children[0].Title);
    }

    [Fact]
    public void A_read_only_source_always_takes_the_file_even_if_the_local_copy_is_newer()
    {
        // Save-disabled sources must never let local edits shadow the shared content.
        var sid = Guid.NewGuid();
        var target = new Folder();
        target.Children.Add(new Template { SyncId = sid, Title = "Local title", LastChange = Newer });

        SyncEngine.Merge(target, FileRoot(FileTemplate(sid, "File title", "<p>f</p>", Older)),
                         "My Sync", Guid.NewGuid(), allowSave: false);

        Assert.Equal("File title", target.Children[0].Title);
    }

    // ---- identity preservation ----

    [Fact]
    public void An_existing_item_object_is_reused_rather_than_replaced()
    {
        // Tree identity is what keeps expansion and selection alive across a refresh; replacing the
        // object would visibly collapse the user's tree on every sync poll.
        var sid = Guid.NewGuid();
        var existing = new Template { SyncId = sid, Title = "Alpha", LastChange = Older };
        var target = new Folder();
        target.Children.Add(existing);

        SyncEngine.Merge(target, FileRoot(FileTemplate(sid, "Alpha renamed", "<p>a</p>", Newer)),
                         "My Sync", Guid.NewGuid(), allowSave: true);

        Assert.Same(existing, target.Children[0]);
        Assert.Equal("Alpha renamed", existing.Title);   // same object, updated content
    }

    [Fact]
    public void A_locally_added_item_keeps_its_own_id_but_gains_the_file_sync_id()
    {
        var sid = Guid.NewGuid();
        var target = new Folder();

        SyncEngine.Merge(target, FileRoot(FileTemplate(sid, "Alpha", "<p>a</p>", Older)),
                         "My Sync", Guid.NewGuid(), allowSave: true);

        Assert.Equal(sid, target.Children[0].SyncId);
        Assert.NotEqual(sid, target.Children[0].Id);   // a fresh local id, not the shared one
    }

    // ---- ordering and nesting ----

    [Fact]
    public void Child_order_follows_the_file()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var target = new Folder();
        target.Children.Add(new Template { SyncId = a, Title = "Alpha", LastChange = Older });
        target.Children.Add(new Template { SyncId = b, Title = "Beta", LastChange = Older });

        SyncEngine.Merge(target, FileRoot(
                             FileTemplate(b, "Beta", "<p>b</p>", Older),
                             FileTemplate(a, "Alpha", "<p>a</p>", Older)),
                         "My Sync", Guid.NewGuid(), allowSave: true);

        Assert.Equal(new[] { "Beta", "Alpha" }, target.Children.Select(c => c.Title));
    }

    [Fact]
    public void Nested_folders_merge_recursively()
    {
        var folderId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        var fileFolder = new Folder { Id = folderId, Title = "Group", LastChange = Older };
        fileFolder.Children.Add(FileTemplate(childId, "Nested", "<p>n</p>", Older));

        var target = new Folder();
        SyncEngine.Merge(target, FileRoot(fileFolder), "My Sync", Guid.NewGuid(), allowSave: true);

        var group = Assert.IsType<Folder>(target.Children[0]);
        Assert.Equal("Group", group.Title);
        var nested = Assert.Single(group.Children);
        Assert.Equal("Nested", nested.Title);
        Assert.Equal(childId, nested.SyncId);
    }

    [Fact]
    public void An_item_moved_between_folders_in_the_file_is_relocated_not_duplicated()
    {
        var movedId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        // Locally the item sits at the root.
        var target = new Folder();
        target.Children.Add(new Template { SyncId = movedId, Title = "Mover", LastChange = Older });

        // In the file it has moved inside a folder.
        var fileFolder = new Folder { Id = groupId, Title = "Group", LastChange = Older };
        fileFolder.Children.Add(FileTemplate(movedId, "Mover", "<p>m</p>", Older));

        SyncEngine.Merge(target, FileRoot(fileFolder), "My Sync", Guid.NewGuid(), allowSave: true);

        var group = Assert.IsType<Folder>(Assert.Single(target.Children));
        Assert.Equal("Mover", Assert.Single(group.Children).Title);
    }

    [Fact]
    public void Merging_an_empty_file_clears_the_target()
    {
        var target = new Folder();
        target.Children.Add(new Template { SyncId = Guid.NewGuid(), Title = "Alpha", LastChange = Older });

        SyncEngine.Merge(target, FileRoot(), "My Sync", Guid.NewGuid(), allowSave: true);

        Assert.Empty(target.Children);
    }

    [Fact]
    public void Merging_is_idempotent()
    {
        var sid = Guid.NewGuid();
        var target = new Folder();
        var fileRoot = FileRoot(FileTemplate(sid, "Alpha", "<p>a</p>", Older));

        SyncEngine.Merge(target, fileRoot, "My Sync", Guid.NewGuid(), allowSave: true);
        var first = target.Children[0];
        SyncEngine.Merge(target, fileRoot, "My Sync", Guid.NewGuid(), allowSave: true);

        Assert.Same(first, Assert.Single(target.Children));
    }
}
