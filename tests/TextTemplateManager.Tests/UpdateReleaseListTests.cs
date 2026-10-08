using System.Text.Json;
using TextTemplateManager.Services.System;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Choosing the update from the release list. From 2.0 on, releases are Store-only (no installer);
/// a 1.x client must skip them and still find the last 1.x release, also after many 2.x releases.</summary>
public class UpdateReleaseListTests
{
    private static readonly UpdateService.ReleaseVer Installed = new(new Version(1, 9, 1), false);

    private static object Rel(string tag, bool withInstaller = true, bool prerelease = false) => new
    {
        tag_name = tag,
        prerelease,
        draft = false,
        assets = withInstaller
            ? new[] { new { name = $"TextTemplateManager-Setup-{tag.TrimStart('v')}.exe", browser_download_url = $"https://example/{tag}.exe" } }
            : new[] { new { name = "TextTemplateManager-Manual.pdf", browser_download_url = "https://example/manual.pdf" } },
    };

    private static JsonElement Page(params object[] releases)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(releases));
        return doc.RootElement.Clone();
    }

    private static (UpdateService.Candidate? best, bool reachedInstalled) Scan(JsonElement page, bool allowBeta = false)
    {
        UpdateService.Candidate? best = null;
        bool reached = UpdateService.ScanReleasePage(page, Installed, allowBeta, ref best);
        return (best, reached);
    }

    [Fact]
    public void Store_only_2x_releases_are_skipped_and_the_last_1x_release_is_offered()
    {
        var (best, _) = Scan(Page(Rel("v2.1.0", withInstaller: false), Rel("v1.10.0"), Rel("v2.0.0", withInstaller: false), Rel("v1.9.1")));

        Assert.Equal("v1.10.0", best!.Tag);
    }

    [Fact]
    public void A_2x_release_is_skipped_even_if_it_carries_a_setup_installer()
    {
        var (best, _) = Scan(Page(Rel("v2.0.0"), Rel("v1.10.0"), Rel("v1.9.1")));

        Assert.Equal("v1.10.0", best!.Tag);
    }

    [Fact]
    public void Reaching_the_installed_version_ends_the_search()
    {
        var (_, reached) = Scan(Page(Rel("v2.0.0", withInstaller: false), Rel("v1.10.0"), Rel("v1.9.1"), Rel("v1.9.0")));

        Assert.True(reached);
    }

    [Fact]
    public void A_page_of_only_newer_releases_asks_for_the_next_page()
    {
        // e.g. 100 Store-only 2.x releases created after v1.10.0: v1.10.0 is on a later page.
        var newer = Enumerable.Range(0, 5).Select(i => Rel($"v2.{i}.0", withInstaller: false)).ToArray();

        var (best, reached) = Scan(Page(newer));

        Assert.Null(best);
        Assert.False(reached);
    }

    [Fact]
    public void The_best_candidate_carries_over_between_pages()
    {
        UpdateService.Candidate? best = null;
        UpdateService.ScanReleasePage(Page(Rel("v2.0.0", withInstaller: false), Rel("v1.10.0")), Installed, false, ref best);
        UpdateService.ScanReleasePage(Page(Rel("v1.9.2"), Rel("v1.9.1")), Installed, false, ref best);

        Assert.Equal("v1.10.0", best!.Tag);
    }

    [Fact]
    public void Betas_are_offered_only_when_allowed()
    {
        var page = Page(Rel("v1.10.0-beta", prerelease: true), Rel("v1.9.2"), Rel("v1.9.1"));

        Assert.Equal("v1.9.2", Scan(page).best!.Tag);
        Assert.Equal("v1.10.0-beta", Scan(page, allowBeta: true).best!.Tag);
    }

    [Fact]
    public void A_newer_1x_release_without_an_installer_is_not_offered()
    {
        var (best, _) = Scan(Page(Rel("v1.10.0", withInstaller: false), Rel("v1.9.1")));

        Assert.Null(best);
    }
}
