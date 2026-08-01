using System;
using System.IO;
using System.Text.Json;
using TextTemplateManager.Data;

namespace TextTemplateManager.Services.System;

/// <summary>Persisted state for an unattended update: which release is armed to install, and how many
/// attempts have already been launched for it. Survives the process, so an install deferred at exit
/// resumes at the next launch and a release that fails to apply can't be reinstalled forever.
///
/// Lives in the installer folder as JSON. <see cref="UpdateService.CleanInstallerDir"/> only ever
/// removes .exe/.part files, so it never deletes this.</summary>
public sealed class UpdateState
{
    /// <summary>Unattended installs launched for one release before the user is asked to take over.</summary>
    public const int MaxAttempts = 2;

    public string Tag { get; set; } = "";           // release tag being installed (e.g. "v1.3.2")
    public string Asset { get; set; } = "";         // installer file name inside the installer folder
    public string FromVersion { get; set; } = "";   // installed version when the attempt was armed
    public int Attempts { get; set; }               // installer launches so far for this tag

    /// <summary>True once the automatic attempts are used up — the update needs a decision from the user.</summary>
    public bool Exhausted => Attempts >= MaxAttempts;

    private static string StatePath() => Path.Combine(StorageService.GetInstallerDir(), "update-state.json");

    public static UpdateState? Load()
    {
        try
        {
            string p = StatePath();
            return File.Exists(p) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(StorageService.GetInstallerDir());
            File.WriteAllText(StatePath(), JsonSerializer.Serialize(this));
        }
        catch { /* best effort — losing this only costs an extra install attempt */ }
    }

    public static void Clear()
    {
        try { File.Delete(StatePath()); } catch { }
    }
}
