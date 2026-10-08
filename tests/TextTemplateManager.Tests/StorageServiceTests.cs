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

    // ---- the app's own files: never overwrite one that couldn't be read ----

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("")]        // truncated to nothing
    [InlineData("null")]
    public async Task An_unreadable_own_file_is_moved_aside_and_reported(string content)
    {
        string dir = TestEnvironment.NewScratchDir();
        string path = Path.Combine(dir, "data.ttmdata");
        await File.WriteAllTextAsync(path, content);
        StorageService.TakeUnreadableFiles();

        var loaded = await StorageService.LoadOwnFileAsync<Folder>(path);

        Assert.Null(loaded);
        Assert.False(File.Exists(path));
        var report = Assert.Single(StorageService.TakeUnreadableFiles());
        Assert.Equal(path, report.Path);
        Assert.NotNull(report.KeptAs);
        Assert.Equal(content, await File.ReadAllTextAsync(report.KeptAs!));
    }

    [Fact]
    public async Task A_missing_own_file_is_not_reported()
    {
        StorageService.TakeUnreadableFiles();

        Assert.Null(await StorageService.LoadOwnFileAsync<Folder>(
            Path.Combine(TestEnvironment.NewScratchDir(), "absent.ttmdata")));
        Assert.Empty(StorageService.TakeUnreadableFiles());
    }

    [Fact]
    public async Task A_readable_own_file_loads_normally()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "data.ttmdata");
        await StorageService.SaveAsync(path, Tree("Root", "Alpha"));
        StorageService.TakeUnreadableFiles();

        var loaded = await StorageService.LoadOwnFileAsync<Folder>(path);

        Assert.Equal("Alpha", Assert.Single(loaded!.Children).Title);
        Assert.Empty(StorageService.TakeUnreadableFiles());
    }

    [Fact]
    public async Task An_own_file_that_can_be_neither_read_nor_moved_is_never_overwritten()
    {
        string path = Path.Combine(TestEnvironment.NewScratchDir(), "data.ttmdata");
        await File.WriteAllTextAsync(path, "original");
        StorageService.TakeUnreadableFiles();

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(await StorageService.LoadOwnFileAsync<Folder>(path));

        var report = Assert.Single(StorageService.TakeUnreadableFiles());
        Assert.Null(report.KeptAs);
        await StorageService.SaveAsync(path, Tree("Root", "Alpha"));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Unreadable_settings_fall_back_to_defaults_but_keep_the_original()
    {
        string path = StorageService.GetSettingsPath();
        await File.WriteAllTextAsync(path, "{ \"PasteWindowHotkey\": ");
        StorageService.TakeUnreadableFiles();

        var settings = await StorageService.LoadSettingsAsync();

        Assert.Equal(new AppSettings().PasteWindowHotkey, settings.PasteWindowHotkey);
        var report = Assert.Single(StorageService.TakeUnreadableFiles());
        Assert.Equal("{ \"PasteWindowHotkey\": ", await File.ReadAllTextAsync(report.KeptAs!));
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

    // ---- atomic writes ----

    [Fact]
    public async Task A_failed_write_leaves_the_previous_file_intact()
    {
        // The temp file can't be created (a folder occupies its name), so the write fails before the
        // swap: the existing file must still hold the last good content, not a truncated one.
        string dir = TestEnvironment.NewScratchDir();
        string path = Path.Combine(dir, "data.ttmdata");
        await StorageService.SaveAsync(path, Tree("Root", "Alpha"));
        Directory.CreateDirectory(path + ".tmp");

        await Assert.ThrowsAnyAsync<Exception>(() => StorageService.SaveAsync(path, Tree("Root", "Alpha", "Beta")));

        var loaded = await StorageService.LoadRootAsync(path);
        Assert.Equal(new[] { "Alpha" }, loaded!.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task A_temp_file_left_by_an_interrupted_write_is_replaced_on_the_next_save()
    {
        string dir = TestEnvironment.NewScratchDir();
        string path = Path.Combine(dir, "data.ttmdata");
        await File.WriteAllTextAsync(path + ".tmp", "{ half written");

        await StorageService.SaveAsync(path, Tree("Root", "Alpha"));

        Assert.NotNull(await StorageService.LoadRootAsync(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public async Task Settings_are_written_atomically_and_reload()
    {
        var settings = new AppSettings { PasteWindowHotkey = "Ctrl+Alt+Q", BrowserConnectorPort = 50123 };

        await StorageService.SaveSettingsAsync(settings);
        var loaded = await StorageService.LoadSettingsAsync();

        Assert.Equal("Ctrl+Alt+Q", loaded.PasteWindowHotkey);
        Assert.Equal(50123, loaded.BrowserConnectorPort);
        Assert.False(File.Exists(StorageService.GetSettingsPath() + ".tmp"));
    }

    [Fact]
    public async Task A_failed_settings_write_keeps_the_previous_settings()
    {
        await StorageService.SaveSettingsAsync(new AppSettings { PasteWindowHotkey = "Ctrl+Alt+K" });
        string tmp = StorageService.GetSettingsPath() + ".tmp";
        Directory.CreateDirectory(tmp);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => StorageService.SaveSettingsAsync(new AppSettings { PasteWindowHotkey = "Ctrl+Alt+L" }));

            Assert.Equal("Ctrl+Alt+K", (await StorageService.LoadSettingsAsync()).PasteWindowHotkey);
        }
        finally { Directory.Delete(tmp); }
    }

    [Fact]
    public async Task Sync_settings_are_written_atomically_and_reload()
    {
        var sync = new SyncSettings { Separator = "." };
        sync.Sources.Add(new SyncSource { Name = "Team", Path = @"C:\shared\team.ttmdata", IsActive = true });

        await StorageService.SaveSyncSettingsAsync(sync);
        var loaded = await StorageService.LoadSyncSettingsAsync();

        Assert.Equal(".", loaded.Separator);
        Assert.Equal("Team", Assert.Single(loaded.Sources).Name);
        Assert.False(File.Exists(StorageService.GetSyncSettingsPath() + ".tmp"));
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
