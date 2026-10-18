using TextTemplateManager.Services.System;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Once TextTemplateManager 2 (the Store version) is installed, this version steps aside: it never
/// starts hidden at login next to it, and it finds the Store package from an unpackaged process.</summary>
public class StoreVersionTests
{
    [Theory]
    [InlineData(true, true, false, true)]     // login autostart next to version 2: leave at once
    [InlineData(true, false, false, false)]   // started by the user: open (and offer the removal)
    [InlineData(true, true, true, false)]     // opening a .ttmdata file: handle it
    [InlineData(false, true, false, false)]   // version 2 not installed: normal hidden start
    [InlineData(false, false, false, false)]
    public void Only_a_hidden_autostart_next_to_version_2_exits(bool installed, bool hidden, bool openingFile, bool exits)
    {
        Assert.Equal(exits, StoreVersion.ExitAtStartup(installed, hidden, openingFile));
    }

    [Theory]
    [InlineData(false, false, true)]    // not installed, not dismissed: show it
    [InlineData(false, true, false)]    // "Don't show this again" ticked
    [InlineData(true, false, false)]    // version 2 installed: the removal offer replaces it
    [InlineData(true, true, false)]
    public void The_store_notice_is_due_until_version_2_is_installed_or_it_is_dismissed(bool installed, bool hidden, bool due)
    {
        Assert.Equal(due, StoreVersion.StoreNoticeDue(installed, hidden));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]    // plain hidden autostart: stays in the tray
    [InlineData(true, false, false, true, false)]    // notice due: the window opens at login so it is seen
    [InlineData(true, true, false, false, false)]    // a file to open
    [InlineData(true, false, true, false, false)]    // an unreadable data file to report
    [InlineData(false, false, false, false, false)]  // normal start
    [InlineData(false, false, false, true, false)]
    public void A_hidden_autostart_opens_the_window_while_the_store_notice_is_due(
        bool hiddenLaunch, bool openingFile, bool unreadable, bool noticeDue, bool staysHidden)
    {
        Assert.Equal(staysHidden, StoreVersion.StartHidden(hiddenLaunch, openingFile, unreadable, noticeDue));
    }

    [Fact]
    public void An_installed_store_package_is_found_by_its_family_name()
    {
        // Windows Settings is a packaged app present on every Windows 10/11 installation.
        Assert.True(StoreVersion.IsPackageInstalled("windows.immersivecontrolpanel_cw5n1h2txyewy"));
    }

    [Fact]
    public void A_package_that_is_not_installed_is_not_found()
    {
        Assert.False(StoreVersion.IsPackageInstalled("MarflowSoftware.DoesNotExist_0000000000000"));
    }
}
