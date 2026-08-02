using System.Runtime.CompilerServices;
using TextTemplateManager.Data;

namespace TextTemplateManager.Tests;

/// <summary>Redirects the app's data root to a throwaway folder before any test touches
/// <see cref="StorageService"/>.
///
/// This is not optional hygiene: StorageService resolves its root in a static field initializer, so the
/// very first access freezes it for the process. Without this running first, tests would read and delete
/// the user's real templates, settings and staged update installers under
/// %LocalAppData%\Marflow Software\TextTemplateManager.
///
/// [ModuleInitializer] is what guarantees the ordering — it runs when the test assembly loads, ahead of
/// any test, fixture or class constructor.</summary>
internal static class TestEnvironment
{
    /// <summary>The per-run data root. Left on disk after the run for post-mortem inspection; it lives
    /// under the temp folder, so it costs nothing.</summary>
    internal static string DataRoot { get; private set; } = "";

    [ModuleInitializer]
    internal static void Initialize()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "ttm-tests", $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DataRoot);
        Environment.SetEnvironmentVariable(StorageService.DataDirOverrideVariable, DataRoot);
    }

    /// <summary>A fresh empty directory for one test, so file-system tests can't interfere with
    /// each other when xUnit runs collections in parallel.</summary>
    internal static string NewScratchDir([CallerMemberName] string name = "")
    {
        string dir = Path.Combine(DataRoot, "scratch", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
