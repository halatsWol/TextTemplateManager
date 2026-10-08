using System.Text.Json;
using TextTemplateManager.Services.System;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Asset selection from a GitHub release payload. Only <c>TextTemplateManager-Setup*.exe</c> is ever
/// treated as the installer: every other .exe on a release (deltas, the support tool) must never be run as
/// one, and from 2.0 on releases carry no installer at all.</summary>
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
    public void Without_a_setup_named_asset_there_is_no_installer()
    {
        // No "first .exe" fallback any more: a release carrying only the support tool (as 2.x releases
        // would if a cleanup .exe were attached) must not be run as the installer.
        var rel = Release(
            ("TextTemplateManager-Support-Cleanup.exe", "https://example/cleanup.exe"),
            ("SomeOldInstaller.exe", "https://example/old.exe"));

        var (url, name) = UpdateService.FindInstallerAsset(rel);

        Assert.Null(url);
        Assert.Null(name);
    }

    [Fact]
    public void A_setup_named_asset_must_be_an_exe()
    {
        var rel = Release(("TextTemplateManager-Setup-1.3.2.zip", "https://example/setup.zip"));

        Assert.Null(UpdateService.FindInstallerAsset(rel).url);
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
