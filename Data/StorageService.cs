using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TextTemplateManager.Models;


namespace TextTemplateManager.Data;

public static class StorageService
{
    // Path: %localappdata%\Marflow Software\TextTemplateManager\
    private static readonly string BaseDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Marflow Software",
        "TextTemplateManager");

    private static readonly string DataFileName = "data.ttmdata";
    private static readonly string SettingsFileName = "settings.ttmsettings";
    private static readonly string SyncSettingsFileName = "sync.ttmsettings";

    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Relaxed escaping keeps '<' '>' '&' literal — readable, and safe for a local file.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static StorageService()
    {
        MigrateLegacyFolder();   // rename the old plural folder before creating the new one
        EnsureDirectories();
    }

    // One-time migration: the data folder was historically named "TextTemplatesManager" (plural).
    // Rename it to the correct singular name so upgrading users keep their templates, settings, and
    // sync config. Runs before EnsureDirectories, so the destination doesn't exist yet.
    private static void MigrateLegacyFolder()
    {
        try
        {
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Marflow Software",
                "TextTemplatesManager");
            if (Directory.Exists(legacy) && !Directory.Exists(BaseDirectory))
                Directory.Move(legacy, BaseDirectory);
        }
        catch { /* best effort — EnsureDirectories still yields a usable folder */ }
    }

    public static void EnsureDirectories()
    {
        if (!Directory.Exists(BaseDirectory))
        {
            Directory.CreateDirectory(BaseDirectory);
        }
    }

    public static string GetDataPath() => Path.Combine(BaseDirectory, DataFileName);
    public static string GetSettingsPath() => Path.Combine(BaseDirectory, SettingsFileName);
    public static string GetSyncSettingsPath() => Path.Combine(BaseDirectory, SyncSettingsFileName);

    /// <summary>Folder where downloaded update installers are staged.</summary>
    public static string GetInstallerDir() => Path.Combine(BaseDirectory, "installer");

    /// <summary>Load the sync config, or a new empty one if none exists.</summary>
    public static async Task<SyncSettings> LoadSyncSettingsAsync()
    {
        string path = GetSyncSettingsPath();
        if (!File.Exists(path)) return new SyncSettings();
        try
        {
            string json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<SyncSettings>(json, _options) ?? new SyncSettings();
        }
        catch
        {
            return new SyncSettings();
        }
    }

    public static async Task SaveSyncSettingsAsync(SyncSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, _options);
        await File.WriteAllTextAsync(GetSyncSettingsPath(), json);
    }

    /// <summary>Save the root Folder to path (local or sync location).</summary>
    public static async Task SaveAsync(string path, Folder root)
    {
        string json = JsonSerializer.Serialize(root, _options);

        // Skip the write if the file already matches, so its modified-time doesn't churn.
        if (File.Exists(path))
        {
            string existing = await File.ReadAllTextAsync(path);
            if (string.Equals(existing, json, StringComparison.Ordinal))
                return;
        }

        string tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    public static async Task<Folder?> LoadRootAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            string json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<Folder>(json, _options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Storage Error: {ex.Message}");
            return null;
        }
    }

    // ---- Shared (non-locking) IO for sync files, which may live on OneDrive ----

    /// <summary>Read a root Folder without locking the file, retrying transient locks (OneDrive).</summary>
    public static async Task<Folder?> LoadRootSharedAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return await JsonSerializer.DeserializeAsync<Folder>(fs, _options);
            }
            catch (IOException) { await Task.Delay(150); }        // transient lock -> retry
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Sync read error: {ex.Message}");
                return null;
            }
        }
        return null;
    }

    private static async Task<string?> ReadAllTextSharedAsync(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return await r.ReadToEndAsync();
        }
        catch { return null; }
    }

    /// <summary>Write a sync file: skip if unchanged, else temp-then-atomic-move, retrying if the
    /// target is briefly locked (OneDrive).</summary>
    public static async Task SaveSharedAsync(string path, Folder root)
    {
        string json = JsonSerializer.Serialize(root, _options);

        string? existing = File.Exists(path) ? await ReadAllTextSharedAsync(path) : null;
        if (string.Equals(existing, json, StringComparison.Ordinal)) return;

        string tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (IOException) { await Task.Delay(200); }        // target locked -> retry
        }
        try { File.Delete(tmp); } catch { /* leave temp if even delete fails */ }
    }

    public static async Task<List<BaseItem>?> LoadAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            using FileStream openStream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<List<BaseItem>>(openStream, _options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Storage Error (Load): {ex.Message}");
            return null;
        }
    }

    public static async Task ExportAsync(string fullPath, object data)
    {
        // This handles the .ttmdata export to any user-selected path
        string json = JsonSerializer.Serialize(data, _options);
        await File.WriteAllTextAsync(fullPath, json);
    }

    public static async Task<Folder?> ImportBackupAsync(string fullPath)
    {
        if (!File.Exists(fullPath)) return null;
        return await LoadRootAsync(fullPath);
    }

    public static async Task SaveSettingsAsync(object settings)
    {
        await SaveGenericAsync(GetSettingsPath(), settings);
    }

    private static async Task SaveGenericAsync(string path, object obj)
    {
        using FileStream createStream = File.Create(path);
        await JsonSerializer.SerializeAsync(createStream, obj, _options);
    }


    public static async Task<AppSettings> LoadSettingsAsync()
    {
        string path = GetSettingsPath();
        if (!File.Exists(path)) return new AppSettings();

        try
        {
            string json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<AppSettings>(json, _options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static async Task SaveSettingsAsync(AppSettings settings)
    {
        string path = GetSettingsPath();
        string json = JsonSerializer.Serialize(settings, _options);
        await File.WriteAllTextAsync(path, json);
    }
}