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
    public void Asset_order_does_not_decide_the_installer()
    {
        // GitHub returns assets ALPHABETICALLY, not in upload order — verified against the real v1.3.1
        // and v1.4.0 releases, where the cleanup tool sorted ahead of Setup despite being uploaded after
        // it. Picking "the first .exe" therefore handed old clients the wrong binary, so the Setup naming
        // decides instead, wherever it appears in the list.
        var rel = Release(
            ("TextTemplateManager-CleanupUtility.exe", "https://example/cleanup.exe"),
            ("TextTemplateManager-Update-1.3.1-to-1.3.2.exe", "https://example/delta.exe"),
            ("TextTemplateManager-Setup-1.3.2.exe", "https://example/full.exe"));

        var (url, name) = UpdateService.FindInstallerAsset(rel);

        Assert.Equal("https://example/full.exe", url);
        Assert.Equal("TextTemplateManager-Setup-1.3.2.exe", name);
    }

    [Fact]
    public void Without_a_setup_named_asset_the_first_exe_is_still_used()
    {
        // Keeps pre-1.2 releases resolvable, whose installer was not named this way.
        var rel = Release(("SomeOldInstaller.exe", "https://example/old.exe"));

        var (url, _) = UpdateService.FindInstallerAsset(rel);

        Assert.Equal("https://example/old.exe", url);
    }

    [Fact]
    public void The_installer_is_found_among_many_delta_assets()
    {
        // A release ships one delta per earlier version in its series, listed alphabetically alongside
        // the support tool. Exactly one of these .exe assets is installable by a client on any version.
        var rel = Release(
            ("TextTemplateManager-Manual.pdf", "https://example/manual.pdf"),
            ("TextTemplateManager-Setup-1.3.9.exe", "https://example/full.exe"),
            ("TextTemplateManager-Support-Cleanup.exe", "https://example/cleanup.exe"),
            ("TextTemplateManager-Update-1.3.0-to-1.3.9.exe", "https://example/d0.exe"),
            ("TextTemplateManager-Update-1.3.8-to-1.3.9.exe", "https://example/d8.exe"));

        var (url, name) = UpdateService.FindInstallerAsset(rel);

        Assert.Equal("https://example/full.exe", url);
        Assert.Equal("TextTemplateManager-Setup-1.3.9.exe", name);
    }

    [Fact]
    public void The_support_cleanup_tool_is_never_mistaken_for_the_installer()
    {
        // The actual defect found in v1.4.0: the cleanup tool sorts before Setup, so a first-.exe pick
        // handed clients a force-uninstaller instead of the installer.
        var rel = Release(
            ("TextTemplateManager-Support-Cleanup.exe", "https://example/cleanup.exe"),
            ("TextTemplateManager-Setup-1.4.1.exe", "https://example/full.exe"));

        var (_, name) = UpdateService.FindInstallerAsset(rel);

        Assert.Equal("TextTemplateManager-Setup-1.4.1.exe", name);
    }

    [Fact]
    public void Every_published_delta_is_resolvable_by_name()
    {
        // Each base version's delta has to be findable individually — that is how a 1.2+ client turns
        // the "from" entry it matched in update.json into a download URL.
        var rel = Release(
            ("TextTemplateManager-Setup-1.3.9.exe", "https://example/full.exe"),
            ("TextTemplateManager-Update-1.3.0-to-1.3.9.exe", "https://example/d0.exe"),
            ("TextTemplateManager-Update-1.3.1-to-1.3.9.exe", "https://example/d1.exe"));

        Assert.Equal("https://example/d0.exe", UpdateService.FindAssetUrl(rel, "TextTemplateManager-Update-1.3.0-to-1.3.9.exe"));
        Assert.Equal("https://example/d1.exe", UpdateService.FindAssetUrl(rel, "TextTemplateManager-Update-1.3.1-to-1.3.9.exe"));
        // A base with no delta published (too old, or dropped for poor savings) resolves to nothing,
        // which is what sends that client to the full installer.
        Assert.Null(UpdateService.FindAssetUrl(rel, "TextTemplateManager-Update-1.2.2-to-1.3.9.exe"));
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
