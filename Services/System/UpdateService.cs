using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using TextTemplateManager.Data;

namespace TextTemplateManager.Services.System;

/// <summary>
/// Checks GitHub Releases for a newer version, downloads its installer, and launches it silently.
/// Auto-update is a no-op for local/dev builds (version "0.0.0-dev"), which have no release version.
/// </summary>
public sealed class UpdateService
{
    // Keep in sync with the repo used by the Help ▸ Go to GitHub link.
    private const string Owner = "halatsWol";
    private const string Repo = "TextTemplateManager";

    public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string AssetName);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TextTemplateManager-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Returns the latest release if it is newer than the installed version; null if this
    /// is up to date, a dev build, or the check failed.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        var installed = GetInstalledVersion();
        if (installed == null) return null; // dev build — don't self-replace

        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        if (!resp.IsSuccessStatusCode) return null; // 404 = no releases yet, etc.

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var latest = ParseVersion(root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);
        if (latest == null || latest <= installed) return null;

        // First .exe asset is the installer.
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && a.TryGetProperty("browser_download_url", out var u) && u.GetString() is string url)
                {
                    return new UpdateInfo(latest, root.GetProperty("tag_name").GetString() ?? "", url, name);
                }
            }
        }
        return null;
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

    /// <summary>The installed release version, or null for a dev/pre-release build.</summary>
    public static Version? GetInstalledVersion()
    {
        string info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = info.IndexOf('+');
        if (plus >= 0) info = info[..plus];
        return ParseVersion(info);
    }

    // Parses "v0.9.3" / "0.9.3"; rejects pre-release/dev strings (containing '-').
    private static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        if (s.Contains('-')) return null;
        return Version.TryParse(s, out var v) ? v : null;
    }
}
