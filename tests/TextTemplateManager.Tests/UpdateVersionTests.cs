using TextTemplateManager.Services.System;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Release-tag parsing and ordering — the logic that decides which release a user is offered.
/// A mistake here either withholds an update or pushes a beta onto someone who opted out, and neither
/// is visible until it reaches users, so the edge cases are pinned down explicitly.</summary>
public class UpdateVersionTests
{
    private static UpdateService.ReleaseVer Parse(string tag, bool ghPrerelease = false)
    {
        var v = UpdateService.ParseRelease(tag, ghPrerelease);
        Assert.NotNull(v);
        return v!;
    }

    // ---- numeric component padding ----

    [Theory]
    [InlineData("2", "2.0.0")]           // bare major
    [InlineData("v2", "2.0.0")]
    [InlineData("1.3", "1.3.0")]         // two components -> padded, so v1.3 == v1.3.0
    [InlineData("v1.3", "1.3.0")]
    [InlineData("1.3.1", "1.3.1")]
    [InlineData("v1.3.1", "1.3.1")]
    [InlineData("1.0.0.1", "1.0.0.1")]   // four components kept
    public void Tag_numeric_part_is_padded_to_at_least_three_components(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), Parse(tag).Numeric);
    }

    [Fact]
    public void Two_and_three_component_forms_of_the_same_release_compare_equal()
    {
        // The release history mixes "v1.2" and "v1.2.0" style tags; they must not look like an update
        // to one another or the app would offer users a pointless reinstall.
        Assert.Equal(0, Parse("v1.2").CompareTo(Parse("v1.2.0")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-digits-here")]
    [InlineData(null)]
    public void Tags_without_a_numeric_part_are_rejected(string? tag)
    {
        Assert.Null(UpdateService.ParseRelease(tag, ghPrerelease: false));
    }

    // ---- pre-release detection ----

    [Theory]
    [InlineData("v1.3.1-beta")]
    [InlineData("v1.3.1-BETA")]      // case-insensitive
    [InlineData("v1.3.1-preview")]   // matched by the "prev" marker
    [InlineData("v1.3.1-prev")]
    [InlineData("v1.3.1-unstable")]
    public void Name_markers_flag_a_prerelease_even_when_github_does_not(string tag)
    {
        Assert.True(Parse(tag, ghPrerelease: false).Prerelease);
    }

    [Fact]
    public void Github_prerelease_flag_alone_marks_a_prerelease()
    {
        Assert.True(Parse("v1.3.1", ghPrerelease: true).Prerelease);
    }

    [Fact]
    public void Explicit_stable_in_the_name_overrides_the_github_prerelease_flag()
    {
        // A release mismarked as pre-release on GitHub can be rescued by tagging it -stable.
        Assert.False(Parse("v1.0.0-stable", ghPrerelease: true).Prerelease);
    }

    [Fact]
    public void Beta_marker_wins_over_stable_when_both_appear()
    {
        // Documents the precedence: the beta/preview/unstable check runs first, so an odd tag like
        // "1.0-beta-stable" stays a pre-release rather than silently going out to everyone.
        Assert.True(Parse("v1.0.0-beta-stable").Prerelease);
    }

    // ---- ordering ----

    [Fact]
    public void Higher_numeric_version_ranks_above_lower()
    {
        Assert.True(Parse("v1.3.2").CompareTo(Parse("v1.3.1")) > 0);
        Assert.True(Parse("v2.0.0").CompareTo(Parse("v1.9.9")) > 0);
    }

    [Fact]
    public void Stable_outranks_its_own_beta_at_the_same_number()
    {
        // The case that made tag comparison necessary elsewhere: same numeric version, different channel.
        Assert.True(Parse("v1.3.1").CompareTo(Parse("v1.3.1-beta")) > 0);
    }

    [Fact]
    public void A_newer_beta_still_outranks_an_older_stable()
    {
        Assert.True(Parse("v1.4.0-beta").CompareTo(Parse("v1.3.1")) > 0);
    }

    [Fact]
    public void Comparing_against_null_ranks_higher()
    {
        Assert.True(Parse("v1.0.0").CompareTo(null) > 0);
    }

    [Fact]
    public void Identical_tags_compare_equal()
    {
        Assert.Equal(0, Parse("v1.3.1-beta").CompareTo(Parse("v1.3.1-beta")));
    }

    // ---- the guard that actually protects users ----

    [Fact]
    public void An_installed_stable_is_not_superseded_by_the_beta_it_came_from()
    {
        // Running 1.3.1 stable, 1.3.1-beta still on the releases page: it must NOT be treated as newer,
        // or a stable user would be walked backwards onto a beta build.
        var installed = Parse("1.3.1");
        Assert.True(Parse("v1.3.1-beta").CompareTo(installed) < 0);
    }
}
