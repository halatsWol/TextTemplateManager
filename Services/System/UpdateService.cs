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
    private sealed record ReleaseVer(Version Numeric, bool Prerelease) : IComparable<ReleaseVer>
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
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=50");
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        UpdateInfo? best = null;
        ReleaseVer? bestVer = null;

        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

            string? tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            bool ghPrerelease = rel.TryGetProperty("prerelease", out var p) && p.GetBoolean();

            var ver = ParseRelease(tag, ghPrerelease);
            if (ver == null) continue;
            if (ver.Prerelease && !allowBeta) continue;         // betas only when enabled
            if (ver.CompareTo(installed) <= 0) continue;        // not newer than installed
            if (bestVer != null && ver.CompareTo(bestVer) <= 0) continue; // keep the highest

            var (url, asset) = FindInstallerAsset(rel);
            if (url == null) continue;                          // release has no installer asset

            best = new UpdateInfo(ver.Numeric, tag ?? "", url, asset!);
            bestVer = ver;
        }

        return best;
    }

    private static (string? url, string? name) FindInstallerAsset(JsonElement release)
    {
        if (release.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && a.TryGetProperty("browser_download_url", out var u) && u.GetString() is string url)
                    return (url, name);
            }
        return (null, null);
    }

    /// <summary>Downloads the installer to the appdata installer folder (skips if already present),
    /// clearing older installers first. Returns the local path.</summary>
    public async Task<string?> EnsureDownloadedAsync(UpdateInfo info)
    {
        string dir = StorageService.GetInstallerDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, info.AssetName);

        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;

        foreach (var old in Directory.EnumerateFiles(dir, "*.exe"))
            try { File.Delete(old); } catch { /* best effort */ }

        using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        string tmp = path + ".part";
        await using (var fs = File.Create(tmp))
            await resp.Content.CopyToAsync(fs);
        File.Move(tmp, path, overwrite: true);

        return path;
    }

    /// <summary>Runs the installer silently (progress bar, no prompts). The installer closes the
    /// running app, updates, and relaunches it. The caller should exit right after this.</summary>
    public static bool LaunchInstaller(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /SP- /NOCANCEL /NORESTART /SUPPRESSMSGBOXES",
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] launch failed: {ex.Message}");
            return false;
        }
    }

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

    private static readonly Regex NumberPart = new(@"\d+(?:\.\d+)*", RegexOptions.Compiled);

    /// <summary>Parses a tag/version like "v0.9.6-beta", "v1.0", "1.0.0.1", or "v1.0.0-stable".
    /// The leading numeric part (1–4 dotted components, padded to at least 3 for comparison) is the
    /// version; pre-release is decided by <paramref name="ghPrerelease"/> or a beta/preview/unstable/
    /// prev marker in the name, unless the name explicitly says "stable".</summary>
    private static ReleaseVer? ParseRelease(string? s, bool ghPrerelease)
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
