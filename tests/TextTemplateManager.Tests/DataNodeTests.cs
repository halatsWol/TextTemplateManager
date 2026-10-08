using TextTemplateManager.Data;
using TextTemplateManager.Models;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Loading a backup replaces the local items only: the pinned sync folders stay, and the
/// replacement is written once, never as an intermediate empty tree.</summary>
[Collection(StorageCollection.Name)]
public class DataNodeTests
{
    private static async Task<(DataNode node, string path)> NewNodeAsync()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "data.ttmdata");
        var node = new DataNode(path);
        await node.InitializeAsync();
        return (node, path);
    }

    [Fact]
    public async Task Loading_a_backup_replaces_local_items_and_keeps_sync_folders()
    {
        var (node, path) = await NewNodeAsync();
        await node.AddItemAsync(new Template { Title = "Old local" });
        var syncRoot = new Folder { Title = "Team", IsSyncRoot = true, SyncId = Guid.Empty };
        node.LocalItems.Insert(0, syncRoot);

        await node.ReplaceLocalItemsAsync(new BaseItem[]
        {
            new Template { Title = "From backup" },
            new Folder { Title = "Sync snapshot in backup", SyncId = Guid.NewGuid() },
        });

        Assert.Same(syncRoot, node.LocalItems[0]);
        var titles = node.LocalItems.Select(i => i.Title).ToList();
        Assert.Contains("From backup", titles);
        Assert.DoesNotContain("Old local", titles);
        Assert.DoesNotContain("Sync snapshot in backup", titles);

        var saved = await StorageService.LoadRootAsync(path);
        Assert.Contains(saved!.Children, c => c.Title == "From backup");
        Assert.DoesNotContain(saved.Children, c => c.Title == "Old local");
    }

    [Fact]
    public async Task A_debounced_edit_is_written_at_once_when_flushed()
    {
        // Exit flushes instead of waiting for the edit debounce, which would be cut off by the exit.
        var (node, path) = await NewNodeAsync();
        var template = new Template { Title = "Before" };
        await node.AddItemAsync(template);

        template.Title = "Edited just before exit";
        await node.FlushPendingSaveAsync();

        var saved = await StorageService.LoadRootAsync(path);
        Assert.Contains(saved!.Children, c => c.Title == "Edited just before exit");
    }

    [Fact]
    public async Task Waiting_for_writes_completes_after_the_last_write()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "busy.ttmdata");
        var writes = Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            StorageService.SaveAsync(path, new Folder { Title = $"Root {i}" })));

        await StorageService.WaitForWritesAsync();

        Assert.True(writes.IsCompleted);
        Assert.False(StorageService.WritesInFlight);
    }

    [Fact]
    public async Task Loading_a_backup_saves_once()
    {
        var (node, _) = await NewNodeAsync();
        await node.AddItemAsync(new Template { Title = "Old local" });
        int saves = 0;
        node.DataSaved += () => Interlocked.Increment(ref saves);

        await node.ReplaceLocalItemsAsync(Enumerable.Range(1, 5).Select(i => (BaseItem)new Template { Title = $"T{i}" }).ToList());

        Assert.Equal(1, saves);
    }
}

[CollectionDefinition(Name)]
public class StorageCollection
{
    // Tests that read or write the shared test data root (settings, sync settings, unreadable-file
    // reports) run one at a time.
    public const string Name = "Storage";
}
