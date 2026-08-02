using TextTemplateManager.Services.System;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Installer-folder housekeeping. This deletes files, and one of them may be an installer that
/// an unattended update is about to run, so "what survives a sweep" is worth pinning down exactly.</summary>
public class InstallerDirTests
{
    [Fact]
    public void The_kept_asset_survives_and_other_installers_are_removed()
    {
        string dir = TestEnvironment.NewScratchDir();
        File.WriteAllText(Path.Combine(dir, "TextTemplateManager-Setup-1.3.2.exe"), "new");
        File.WriteAllText(Path.Combine(dir, "TextTemplateManager-Setup-1.3.1.exe"), "old");
        File.WriteAllText(Path.Combine(dir, "TextTemplateManager-Update-1.3.0-to-1.3.1.exe"), "old delta");

        UpdateService.CleanInstallerDir(dir, keep: "TextTemplateManager-Setup-1.3.2.exe");

        Assert.True(File.Exists(Path.Combine(dir, "TextTemplateManager-Setup-1.3.2.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "TextTemplateManager-Setup-1.3.1.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "TextTemplateManager-Update-1.3.0-to-1.3.1.exe")));
    }

    [Fact]
    public void Interrupted_partial_downloads_are_removed()
    {
        // The old sweep globbed *.exe, which never matched "...exe.part", so abandoned downloads
        // accumulated forever.
        string dir = TestEnvironment.NewScratchDir();
        File.WriteAllText(Path.Combine(dir, "TextTemplateManager-Setup-1.3.2.exe.part"), "half");

        UpdateService.CleanInstallerDir(dir, keep: null);

        Assert.False(File.Exists(Path.Combine(dir, "TextTemplateManager-Setup-1.3.2.exe.part")));
    }

    [Fact]
    public void The_persisted_update_state_is_never_swept()
    {
        // update-state.json lives in this same folder and carries the retry cap. Deleting it would hand
        // a release that fails to install an unlimited supply of fresh attempts.
        string dir = TestEnvironment.NewScratchDir();
        string state = Path.Combine(dir, "update-state.json");
        File.WriteAllText(state, """{"Tag":"v1.3.2","Attempts":2}""");
        File.WriteAllText(Path.Combine(dir, "TextTemplateManager-Setup-1.3.2.exe"), "installer");

        UpdateService.CleanInstallerDir(dir, keep: null);

        Assert.True(File.Exists(state));
        Assert.False(File.Exists(Path.Combine(dir, "TextTemplateManager-Setup-1.3.2.exe")));
    }

    [Fact]
    public void Keep_matching_is_case_insensitive()
    {
        string dir = TestEnvironment.NewScratchDir();
        File.WriteAllText(Path.Combine(dir, "TextTemplateManager-Setup-1.3.2.exe"), "installer");

        UpdateService.CleanInstallerDir(dir, keep: "texttemplatemanager-setup-1.3.2.EXE");

        Assert.True(File.Exists(Path.Combine(dir, "TextTemplateManager-Setup-1.3.2.exe")));
    }

    [Fact]
    public void A_missing_directory_is_not_an_error()
    {
        // Called on the up-to-date path, which can run before the folder has ever been created.
        UpdateService.CleanInstallerDir(Path.Combine(TestEnvironment.NewScratchDir(), "nope"), keep: null);
    }

    [Fact]
    public void A_locked_installer_does_not_abort_the_sweep()
    {
        string dir = TestEnvironment.NewScratchDir();
        string locked = Path.Combine(dir, "locked-1.3.1.exe");
        File.WriteAllText(locked, "in use");
        File.WriteAllText(Path.Combine(dir, "deletable-1.3.0.exe"), "free");

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            UpdateService.CleanInstallerDir(dir, keep: null);

        Assert.True(File.Exists(locked));                                        // couldn't be deleted
        Assert.False(File.Exists(Path.Combine(dir, "deletable-1.3.0.exe")));     // but the rest still went
    }
}
