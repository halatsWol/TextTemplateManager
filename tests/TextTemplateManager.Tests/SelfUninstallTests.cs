using TextTemplateManager.Services.System;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>"Remove this version" runs the installer's own uninstaller, but only the one belonging to the copy
/// that is running: a dev or portable build must never remove a different, installed copy.</summary>
public class SelfUninstallTests
{
    private const string AppDir = @"C:\Users\u\AppData\Local\Programs\Marflow Software\TextTemplateManager";

    [Theory]
    [InlineData("\"" + AppDir + "\\unins000.exe\"")]
    [InlineData(AppDir + "\\unins000.exe")]
    [InlineData("  \"" + AppDir + "\\unins000.exe\"  ")]
    public void The_uninstaller_of_the_running_copy_is_used(string uninstallString)
    {
        Assert.Equal(AppDir + @"\unins000.exe", SelfUninstall.UninstallerFor(uninstallString, AppDir + @"\"));
    }

    [Fact]
    public void An_uninstaller_of_another_copy_is_never_used()
    {
        // e.g. a dev build in the repo's bin folder while the installed copy is registered
        Assert.Null(SelfUninstall.UninstallerFor("\"" + AppDir + "\\unins000.exe\"", @"C:\src\ttm\bin\x64\Debug\"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"C:\\unterminated\\unins000.exe")]
    public void No_usable_uninstall_entry_means_no_uninstaller(string? uninstallString)
    {
        Assert.Null(SelfUninstall.UninstallerFor(uninstallString, AppDir));
    }
}
