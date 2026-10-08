using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TextTemplateManager.Data;

namespace TextTemplateManager.Services.System;

/// <summary>
/// Checks GitHub Releases for a newer version, downloads its installer, and launches it silently.
/// Auto-update is a no-op for local/dev builds (version "0.0.0-dev"), which have no release version.
///
/// Stable releases are always offered. A release is treated as a pre-release (beta) when GitHub
/// marks it as a pre-release, or when its tag/name contains "beta", "preview", "unstable", or
/// "prev" — unless the name says "stable". Pre-releases are offered only when the caller passes
/// <c>allowBeta = true</c> (the "Allow beta updates" setting).
/// </summary>
public sealed class UpdateService
{
    // Keep in sync with the repo used by the Help ▸ Go to GitHub link.
    private const string Owner = "halatsWol";
    private const string Repo = "TextTemplateManager";

    public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string AssetName);

    // A comparable release version: numeric part plus whether it is a pre-release/beta.
    // internal (not private) so the test suite can exercise the ordering rules directly.
    internal sealed record ReleaseVer(Version Numeric, bool Prerelease) : IComparable<ReleaseVer>
    {
        public int CompareTo(ReleaseVer? other)
        {
            if (other is null) return 1;
            int c = Numeric.CompareTo(other.Numeric);
            if (c != 0) return c;
            // Same numeric version: a stable release outranks a pre-release of the same version.
            return Prerelease == other.Prerelease ? 0 : (Prerelease ? -1 : 1);
        }
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TextTemplateManager-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Returns the newest applicable release that is newer than the installed version, or
    /// null if up to date / a dev build / the check failed. Pre-releases are considered only when
    /// <paramref name="allowBeta"/> is true.</summary>
    public async Task<UpdateInfo?> CheckAsync(bool allowBeta)
    {
        var installed = GetInstalledVersion();
        if (installed == null) return null; // dev/local build — don't self-replace

        // List releases (not /latest) so pre-releases are visible and name-based markers are
        // honored even for the "latest" release. Drafts aren't returned to unauthenticated callers.
        // Pages are newest first; the next one is only needed while no release at or below the
        // installed version has shown up yet (usually never, as page 1 holds 100 releases).
        Candidate? best = null;
        for (int page = 1; ; page++)
        {
            using var resp = await Http.GetAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page={ReleasePageSize}&page={page}");
            if (!resp.IsSuccessStatusCode)
            {
                if (page == 1) return null;
                break;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            bool reachedInstalled = ScanReleasePage(doc.RootElement, installed, allowBeta, ref best);
            if (reachedInstalled || doc.RootElement.GetArrayLength() < ReleasePageSize) break;
        }

        if (best == null) return null;

        // Prefer a delta built for exactly this installed version (declared in the release's
        // update.json), else the full installer. The download/run path is identical — a delta is
        // just a smaller installer — so only the chosen asset differs here.
        var (dlUrl, dlAsset) = await ResolveDownloadAsync(best.Release, best.Url, best.Asset);
        return new UpdateInfo(best.Version.Numeric, best.Tag ?? "", dlUrl, dlAsset);
    }

    internal const int ReleasePageSize = 100;   // GitHub's maximum

    internal sealed record Candidate(JsonElement Release, ReleaseVer Version, string? Tag, string Url, string Asset);

    /// <summary>Picks the best update from one page of the release list into <paramref name="best"/>.
    /// 2.x and later releases are skipped (they ship through the Microsoft Store, not as an installer).
    /// Returns true once the page holds a release at or below the installed version: pages are newest
    /// first, so later pages can't contain an update.</summary>
    internal static bool ScanReleasePage(JsonElement releases, ReleaseVer installed, bool allowBeta, ref Candidate? best)
    {
        bool reachedInstalled = false;
        foreach (var rel in releases.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

            string? tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            bool ghPrerelease = rel.TryGetProperty("prerelease", out var p) && p.GetBoolean();

            var ver = ParseRelease(tag, ghPrerelease);
            if (ver == null) continue;
            if (ver.Numeric.Major >= 2) continue;
            if (ver.CompareTo(installed) <= 0) { reachedInstalled = true; continue; }   // not newer
            if (ver.Prerelease && !allowBeta) continue;                                  // betas only when enabled
            if (best != null && ver.CompareTo(best.Version) <= 0) continue;              // keep the highest

            var (url, asset) = FindInstallerAsset(rel);
            if (url == null || asset == null) continue;                                  // no installer asset

            best = new Candidate(rel.Clone(), ver, tag, url, asset);
        }
        return reachedInstalled;
    }

    /// <summary>Chooses the asset to download for a release: a delta whose <c>from</c> equals this
    /// installed version (per the release's <c>update.json</c>) when available and present, otherwise
    /// the full installer. Any missing metadata, mismatch, or error falls back to the full installer,
    /// so a delta can never be applied to the wrong base from here.</summary>
    private async Task<(string url, string asset)> ResolveDownloadAsync(JsonElement release, string fallbackUrl, string fallbackAsset)
    {
        string? updateJsonUrl = FindAssetUrl(release, "update.json");
        if (updateJsonUrl == null) return (fallbackUrl, fallbackAsset);   // pre-1.2 release: full only

        try
        {
            using var r = await Http.GetAsync(updateJsonUrl);
            if (!r.IsSuccessStatusCode) return (fallbackUrl, fallbackAsset);
            using var meta = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = meta.RootElement;

            // The full installer named authoritatively by update.json (avoids picking a delta .exe
            // as "the first .exe"); fall back to whatever FindInstallerAsset found otherwise.
            string url = fallbackUrl, asset = fallbackAsset;
            if (root.TryGetProperty("full", out var fj) && fj.GetString() is string full && full.Length > 0
                && FindAssetUrl(release, full) is string fu)
            {
                url = fu; asset = full;
            }

            string? myVer = GetInstalledVersionString();
            if (myVer != null && root.TryGetProperty("deltas", out var deltas) && deltas.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in deltas.EnumerateArray())
                {
                    string? from = d.TryGetProperty("from", out var f) ? f.GetString() : null;
                    string? deltaAsset = d.TryGetProperty("asset", out var a) ? a.GetString() : null;
                    if (string.Equals(from, myVer, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(deltaAsset)
                        && FindAssetUrl(release, deltaAsset) is string durl)
                        return (durl, deltaAsset!);   // delta built for exactly this version
                }
            }
            return (url, asset);
        }
        catch { return (fallbackUrl, fallbackAsset); }
    }

    internal static string? FindAssetUrl(JsonElement release, string assetName)
    {
        if (release.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), assetName, StringComparison.OrdinalIgnoreCase)
                    && a.TryGetProperty("browser_download_url", out var u) && u.GetString() is string url)
                    return url;
        return null;
    }

    private const string SetupAssetPrefix = "TextTemplateManager-Setup";

    /// <summary>The full installer asset: only <c>TextTemplateManager-Setup*.exe</c>. A release carries other
    /// .exe assets (deltas, the support/cleanup tool) and GitHub lists assets alphabetically, so anything
    /// else must never be run as the installer; a release without one has no installer.</summary>
    internal static (string? url, string? name) FindInstallerAsset(JsonElement release)
    {
        if (release.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name.StartsWith(SetupAssetPrefix, StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && a.TryGetProperty("browser_download_url", out var u) && u.GetString() is string url)
                    return (url, name);
            }
        return (null, null);
    }

    /// <summary>Downloads the installer to the appdata installer folder (skips if already present) and
    /// clears superseded installers once the new one is safely in place. Returns the local path.
    /// Asset names carry the version, so an existing file always belongs to the release it is named for.</summary>
    public async Task<string?> EnsureDownloadedAsync(UpdateInfo info)
    {
        string dir = StorageService.GetInstallerDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, info.AssetName);

        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;

        using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long? expected = resp.Content.Headers.ContentLength;

        string tmp = path + ".part";
        await using (var fs = File.Create(tmp))
            await resp.Content.CopyToAsync(fs);

        // Size is the only integrity signal available (GitHub publishes no asset digest). Without it a
        // truncated download would be cached as valid forever and every install attempt would fail.
        long got = new FileInfo(tmp).Length;
        if (expected is long want && got != want)
        {
            try { File.Delete(tmp); } catch { }
            throw new IOException($"download incomplete ({got}/{want} bytes)");
        }

        File.Move(tmp, path, overwrite: true);
        CleanInstallerDir(dir, keep: info.AssetName);   // only now — never delete an installer still pending
        return path;
    }

    /// <summary>Deletes installers and interrupted partial downloads from the installer folder, except
    /// <paramref name="keep"/>. Deliberately called only once a replacement is in place (or nothing is
    /// pending at all), so an armed unattended install can't have its installer deleted out from under it.</summary>
    public static void CleanInstallerDir(string dir, string? keep)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (keep != null && string.Equals(name, keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
        catch { }
    }

    /// <summary>Runs the installer silently (progress bar, no prompts). The installer closes the
    /// running app, updates, and relaunches it. The caller should exit right after this.
    /// <paramref name="relaunchHidden"/> makes the relaunch go straight to the tray — used for an
    /// unattended install, so an update never pops a window over what the user is doing.</summary>
    public static bool LaunchInstaller(string installerPath, bool relaunchHidden = false)
    {
        if (!File.Exists(installerPath))
        {
            Debug.WriteLine($"[Update] installer missing: {installerPath}");
            return false;
        }
        try
        {
            string args = "/SILENT /SP- /NOCANCEL /NORESTART /SUPPRESSMSGBOXES";
            if (relaunchHidden) args += " /HIDDENRELAUNCH=1";
            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = args,
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] launch failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The installed release version exactly as embedded from the tag, or "" for a dev build.
    /// Used to tell whether an update attempt actually changed the running version.</summary>
    public static string InstalledVersionString() => GetInstalledVersionString() ?? "";

    /// <summary>The installed release version (numeric + pre-release flag), or null for a dev build.</summary>
    private static ReleaseVer? GetInstalledVersion()
    {
        string info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = info.IndexOf('+');
        if (plus >= 0) info = info[..plus];
        if (info.Contains("dev", StringComparison.OrdinalIgnoreCase)) return null; // 0.0.0-dev / local

        var v = ParseRelease(info, ghPrerelease: false);
        return v != null && v.Numeric > new Version(0, 0, 0) ? v : null;
    }

    /// <summary>The installed version string exactly as embedded from the release tag (e.g. "1.2",
    /// "1.2.0", "1.2-beta", "1.2.0-beta"), or null for a dev build. This is the raw key a delta's
    /// <c>from</c> is matched against — kept verbatim (not numeric-normalized) so every tag shape
    /// round-trips.</summary>
    private static string? GetInstalledVersionString()
    {
        string info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = info.IndexOf('+');
        if (plus >= 0) info = info[..plus];
        info = info.Trim();
        if (info.Length == 0 || info.Contains("dev", StringComparison.OrdinalIgnoreCase)) return null;
        return info;
    }

    private static readonly Regex NumberPart = new(@"\d+(?:\.\d+)*", RegexOptions.Compiled);

    /// <summary>Parses a tag/version like "v0.9.6-beta", "v1.0", "1.0.0.1", or "v1.0.0-stable".
    /// The leading numeric part (1–4 dotted components, padded to at least 3 for comparison) is the
    /// version; pre-release is decided by <paramref name="ghPrerelease"/> or a beta/preview/unstable/
    /// prev marker in the name, unless the name explicitly says "stable".</summary>
    internal static ReleaseVer? ParseRelease(string? s, bool ghPrerelease)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        var m = NumberPart.Match(s);
        if (!m.Success) return null;
        string num = m.Value;
        int dots = num.Split('.').Length - 1;
        if (dots == 0) num += ".0.0";           // "2" -> "2.0.0"
        else if (dots == 1) num += ".0";        // "1.0" -> "1.0.0" (so it equals "1.0.0")
        if (!Version.TryParse(num, out var v)) return null;

        string lower = s.ToLowerInvariant();
        bool prerelease;
        if (lower.Contains("beta") || lower.Contains("prev") || lower.Contains("unstable"))
            prerelease = true;                  // "prev" also matches "preview"
        else if (lower.Contains("stable"))
            prerelease = false;                 // explicit stable, e.g. v1.0.0-stable
        else
            prerelease = ghPrerelease;

        return new ReleaseVer(v, prerelease);
    }
}
