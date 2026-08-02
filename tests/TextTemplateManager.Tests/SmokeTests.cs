using TextTemplateManager.Data;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Guards the two things the whole suite depends on: that the app assembly's non-UI types load
/// in a plain test host, and that the data root really is redirected away from the user's files.</summary>
public class SmokeTests
{
    [Fact]
    public void DataRoot_is_redirected_away_from_the_real_user_folder()
    {
        string real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Marflow Software", "TextTemplateManager");

        Assert.StartsWith(TestEnvironment.DataRoot, StorageService.GetInstallerDir(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(real, StorageService.GetInstallerDir(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void App_assembly_non_ui_types_load()
    {
        Assert.NotNull(Services.System.UpdateService.ParseRelease("v1.0.0", ghPrerelease: false));
    }
}
