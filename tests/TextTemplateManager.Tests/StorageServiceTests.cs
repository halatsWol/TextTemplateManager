using TextTemplateManager.Data;
using TextTemplateManager.Models;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Persistence behaviour that other features quietly depend on: the skip-if-unchanged rule that
/// keeps cloud-synced files from churning into conflict copies, the per-path write lock, and the
/// write-activity signals the unattended updater uses to decide it is safe to end the process.</summary>
public class StorageServiceTests
{
    /// <summary>Ids are normally random per instance, which would make two logically identical trees
    /// serialise differently and defeat the skip-if-unchanged check. Deriving them from the title keeps
    /// the fixture stable across calls — matching production, where the same objects are reused.</summary>
    private static Guid StableId(string seed) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(seed)));

    private static Folder Tree(string title, params string[] templateTitles)
    {
        var root = new Folder { Id = StableId(title), Title = title, LastChange = "20260101120000" };
        foreach (var t in templateTitles)
            root.Children.Add(new Template
            {
                Id = StableId(t),
                Title = t,
                Content = $"<p>{t}</p>",
                LastChange = "20260101120000",
            });
        return root;
    }

    // ---- round trip ----

    [Fact]
    public async Task Saved_tree_reloads_with_its_structure_intact()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "data.ttmdata");

        await StorageService.SaveAsync(path, Tree("Root", "Alpha", "Beta"));
        var loaded = await StorageService.LoadRootAsync(path);

        Assert.NotNull(loaded);
        Assert.Equal("Root", loaded!.Title);
        Assert.Equal(new[] { "Alpha", "Beta" }, loaded.Children.Select(c => c.Title));
        Assert.Equal("<p>Alpha</p>", Assert.IsType<Template>(loaded.Children[0]).Content);
    }

    [Fact]
    public async Task Loading_a_file_that_does_not_exist_returns_null()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "absent.ttmdata");

        Assert.Null(await StorageService.LoadRootAsync(path));
    }

    [Fact]
    public async Task Loading_a_corrupt_file_returns_null_rather_than_throwing()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "corrupt.ttmdata");
        await File.WriteAllTextAsync(path, "{ not json at all");

        Assert.Null(await StorageService.LoadRootAsync(path));
    }

    // ---- the anti-churn rule ----

    [Fact]
    public async Task Rewriting_identical_content_leaves_the_file_untouched()
    {
        // The fix behind the OneDrive conflict copies: an unchanged save must not bump the modified time,
        // or every device rewrites the shared file on load and they fight each other.
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "shared.ttmdata");
        await StorageService.SaveAsync(path, Tree("Root", "Alpha"));
        var before = File.GetLastWriteTimeUtc(path);

        await Task.Delay(50);
        await StorageService.SaveAsync(path, Tree("Root", "Alpha"));

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Changed_content_does_rewrite_the_file()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "shared.ttmdata");
        await StorageService.SaveAsync(path, Tree("Root", "Alpha"));

        await StorageService.SaveAsync(path, Tree("Root", "Alpha", "Beta"));
        var loaded = await StorageService.LoadRootAsync(path);

        Assert.Equal(2, loaded!.Children.Count);
    }

    [Fact]
    public async Task Shared_save_applies_the_same_skip_if_unchanged_rule()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "sync.ttmdata");
        await StorageService.SaveSharedAsync(path, Tree("Root", "Alpha"));
        var before = File.GetLastWriteTimeUtc(path);

        await Task.Delay(50);
        await StorageService.SaveSharedAsync(path, Tree("Root", "Alpha"));

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Shared_save_leaves_no_temp_file_behind()
    {
        string dir = TestEnvironment.NewScratchDir();
        await StorageService.SaveSharedAsync(Path.Combine(dir, "sync.ttmdata"), Tree("Root", "Alpha"));

        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    // ---- concurrency ----

    [Fact]
    public async Task Concurrent_saves_to_one_path_do_not_collide()
    {
        // Without the per-path lock these overlap and trip ERROR_SHARING_VIOLATION.
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "contended.ttmdata");

        await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => StorageService.SaveAsync(path, Tree("Root", $"Item{i}"))));

        Assert.NotNull(await StorageService.LoadRootAsync(path));
    }

    // ---- write-activity signals used by the unattended updater ----

    [Fact]
    public async Task No_writes_means_quiet_and_nothing_in_flight()
    {
        await Task.Delay(2100);   // outlast the default quiet window

        Assert.False(StorageService.WritesInFlight);
        Assert.True(StorageService.WritesQuiet());
    }

    [Fact]
    public async Task A_write_is_visible_as_in_flight_while_it_runs()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "busy.ttmdata");
        bool sawInFlight = false;

        var writes = Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => StorageService.SaveAsync(path, Tree("Root", $"Item{i}"))));
        while (!writes.IsCompleted)
        {
            if (StorageService.WritesInFlight) { sawInFlight = true; break; }
            await Task.Yield();
        }
        await writes;

        Assert.True(sawInFlight, "a burst of saves never reported WritesInFlight");
    }

    [Fact]
    public async Task A_just_finished_write_is_not_yet_quiet()
    {
        // The distinction that matters: the sync poll writes with no user input, so "idle" alone is not
        // enough to declare it safe to end the process — recent write activity has to count too.
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "recent.ttmdata");
        await StorageService.SaveAsync(path, Tree("Root", "Alpha"));

        Assert.False(StorageService.WritesQuiet(2000));
        Assert.False(StorageService.WritesInFlight);   // finished, but still too recent to be quiet
    }

    [Fact]
    public async Task Quiet_returns_once_the_window_elapses()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "settling.ttmdata");
        await StorageService.SaveAsync(path, Tree("Root", "Alpha"));

        await Task.Delay(250);

        Assert.False(StorageService.WritesQuiet(2000));   // still inside a long window
        Assert.True(StorageService.WritesQuiet(100));     // but past a short one
    }
}
