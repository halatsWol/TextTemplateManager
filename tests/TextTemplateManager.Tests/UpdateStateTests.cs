using TextTemplateManager.Data;
using TextTemplateManager.Services.System;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>The persisted unattended-install record. This is the only thing standing between a release
/// that fails to apply and an endless reinstall loop, so it has to survive a restart, tolerate a corrupt
/// file, and count attempts accurately.
///
/// Serialised, because every case shares the one state file under the redirected data root.</summary>
[Collection(nameof(UpdateStateTests))]
[CollectionDefinition(nameof(UpdateStateTests), DisableParallelization = true)]
public class UpdateStateTests : IDisposable
{
    private static string StateFile => Path.Combine(StorageService.GetInstallerDir(), "update-state.json");

    public UpdateStateTests() => UpdateState.Clear();
    public void Dispose() => UpdateState.Clear();

    [Fact]
    public void Nothing_persisted_means_nothing_armed()
    {
        Assert.Null(UpdateState.Load());
    }

    [Fact]
    public void Save_then_load_round_trips_every_field()
    {
        new UpdateState
        {
            Tag = "v1.3.2",
            Asset = "TextTemplateManager-Setup-1.3.2.exe",
            FromVersion = "1.3.1",
            Attempts = 1,
        }.Save();

        var loaded = UpdateState.Load();

        Assert.NotNull(loaded);
        Assert.Equal("v1.3.2", loaded!.Tag);
        Assert.Equal("TextTemplateManager-Setup-1.3.2.exe", loaded.Asset);
        Assert.Equal("1.3.1", loaded.FromVersion);
        Assert.Equal(1, loaded.Attempts);
    }

    [Fact]
    public void Save_creates_the_installer_directory_if_it_is_missing()
    {
        string dir = StorageService.GetInstallerDir();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        new UpdateState { Tag = "v1.3.2" }.Save();

        Assert.True(File.Exists(StateFile));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(UpdateState.MaxAttempts, true)]
    [InlineData(UpdateState.MaxAttempts + 1, true)]
    public void Exhausted_flips_at_the_attempt_cap(int attempts, bool expected)
    {
        Assert.Equal(expected, new UpdateState { Attempts = attempts }.Exhausted);
    }

    [Fact]
    public void Attempts_accumulate_across_saves()
    {
        // Mirrors the real flow: each unattended launch increments and re-saves before the app exits,
        // so the count has to survive being reloaded rather than resetting.
        new UpdateState { Tag = "v1.3.2", Attempts = 0 }.Save();

        for (int i = 0; i < UpdateState.MaxAttempts; i++)
        {
            var s = UpdateState.Load()!;
            s.Attempts++;
            s.Save();
        }

        Assert.True(UpdateState.Load()!.Exhausted);
    }

    [Fact]
    public void A_corrupt_state_file_reads_as_nothing_armed_instead_of_throwing()
    {
        // A truncated write must not take the app's startup path down with it.
        Directory.CreateDirectory(StorageService.GetInstallerDir());
        File.WriteAllText(StateFile, "{ this is not json");

        Assert.Null(UpdateState.Load());
    }

    [Fact]
    public void An_empty_state_file_reads_as_nothing_armed()
    {
        Directory.CreateDirectory(StorageService.GetInstallerDir());
        File.WriteAllText(StateFile, "");

        Assert.Null(UpdateState.Load());
    }

    [Fact]
    public void Clear_removes_the_record()
    {
        new UpdateState { Tag = "v1.3.2" }.Save();
        Assert.NotNull(UpdateState.Load());

        UpdateState.Clear();

        Assert.Null(UpdateState.Load());
        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public void Clearing_when_nothing_is_persisted_is_harmless()
    {
        UpdateState.Clear();
        UpdateState.Clear();
    }
}
