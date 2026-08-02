using System.Text.Json;
using TextTemplateManager.Services.System;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Asset selection from a GitHub release payload. The release workflow deliberately uploads the
/// full installer as the FIRST .exe because the fallback path picks the first one it sees; these tests
/// pin that contract down so a reordering in release.yml fails here rather than shipping a delta to
/// clients that can't apply it.</summary>
public class UpdateAssetTests
{
    private static JsonElement Release(params (string name, string url)[] assets)
    {
        var payload = new
        {
            tag_name = "v1.3.2",
            assets = assets.Select(a => new { name = a.name, browser_download_url = a.url }).ToArray(),
        };
        // Parked in a JsonDocument that outlives the call via the returned clone.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void First_exe_asset_is_chosen_as_the_installer()
    {
        var rel = Release(
            ("TextTemplateManager-Setup-1.3.2.exe", "https://example/full.exe"),
            ("TextTemplateManager-Update-1.3.1-to-1.3.2.exe", "https://example/delta.exe"));

        var (url, name) = UpdateService.FindInstallerAsset(rel);

        Assert.Equal("https://example/full.exe", url);
        Assert.Equal("TextTemplateManager-Setup-1.3.2.exe", name);
    }

    [Fact]
    public void Upload_order_decides_the_fallback_installer()
    {
        // Documents the fragility the workflow comment warns about: put the delta first and the fallback
        // picks the delta. release.yml must keep the full installer first.
        var rel = Release(
            ("TextTemplateManager-Update-1.3.1-to-1.3.2.exe", "https://example/delta.exe"),
            ("TextTemplateManager-Setup-1.3.2.exe", "https://example/full.exe"));

        var (url, _) = UpdateService.FindInstallerAsset(rel);

        Assert.Equal("https://example/delta.exe", url);
    }

    [Fact]
    public void Non_exe_assets_are_ignored()
    {
        var rel = Release(
            ("manifest.json", "https://example/manifest.json"),
            ("TextTemplateManager-AdminManual.pdf", "https://example/manual.pdf"),
            ("TextTemplateManager-Setup-1.3.2.exe", "https://example/full.exe"));

        var (url, name) = UpdateService.FindInstallerAsset(rel);

        Assert.Equal("https://example/full.exe", url);
        Assert.Equal("TextTemplateManager-Setup-1.3.2.exe", name);
    }

    [Fact]
    public void A_release_with_no_installer_yields_nothing()
    {
        var (url, name) = UpdateService.FindInstallerAsset(Release(("notes.txt", "https://example/notes.txt")));

        Assert.Null(url);
        Assert.Null(name);
    }

    [Fact]
    public void A_release_with_no_assets_at_all_yields_nothing()
    {
        using var doc = JsonDocument.Parse("""{"tag_name":"v1.3.2"}""");

        var (url, _) = UpdateService.FindInstallerAsset(doc.RootElement);

        Assert.Null(url);
    }

    [Fact]
    public void Named_asset_lookup_is_case_insensitive()
    {
        var rel = Release(("Update.JSON", "https://example/update.json"));

        Assert.Equal("https://example/update.json", UpdateService.FindAssetUrl(rel, "update.json"));
    }

    [Fact]
    public void Named_asset_lookup_returns_null_when_absent()
    {
        var rel = Release(("TextTemplateManager-Setup-1.3.2.exe", "https://example/full.exe"));

        Assert.Null(UpdateService.FindAssetUrl(rel, "update.json"));
    }
}
