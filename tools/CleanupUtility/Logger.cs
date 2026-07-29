using System.Text;

namespace TtmCleanup;

/// <summary>Writes a timestamped log, echoing to the console when appropriate. Resolves the log file
/// with a fallback chain (requested path -> default location -> %TEMP%) and creates missing folders.</summary>
internal sealed class Logger : IDisposable
{
    private readonly LogLevel _level;
    private readonly bool _echo;
    private StreamWriter? _writer;
    public string? FilePath { get; private set; }
    public bool HadWarnings { get; private set; }

    public Logger(string? requestedPath, string defaultDir, string tag, LogLevel level, bool echoConsole)
    {
        _level = level;
        _echo = echoConsole;

        string fileName = $"ttm-cleanup-{(string.IsNullOrEmpty(tag) ? "" : tag + "-")}{DateTime.Now:yyyyMMdd-HHmmss}.log";

        foreach (var candidate in ResolveCandidates(requestedPath, defaultDir, fileName))
        {
            try
            {
                var dir = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _writer = new StreamWriter(new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                FilePath = candidate;
                break;
            }
            catch { /* try the next candidate */ }
        }
    }

    // Ordered log-file candidates. A requested folder (or trailing slash) gets the default filename.
    private static IEnumerable<string> ResolveCandidates(string? requested, string defaultDir, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string r = requested!;
            bool looksLikeDir = r.EndsWith('\\') || r.EndsWith('/') || Directory.Exists(r) || string.IsNullOrEmpty(Path.GetFileName(r));
            string full;
            try { full = Path.GetFullPath(looksLikeDir ? Path.Combine(r, fileName) : r); }
            catch { full = ""; }
            if (!string.IsNullOrEmpty(full)) yield return full;
        }
        if (!string.IsNullOrWhiteSpace(defaultDir))
        {
            string full; try { full = Path.GetFullPath(Path.Combine(defaultDir, fileName)); } catch { full = ""; }
            if (!string.IsNullOrEmpty(full)) yield return full;
        }
        yield return Path.Combine(Path.GetTempPath(), fileName);
    }

    private void Write(LogLevel min, string tag, string msg)
    {
        if (min > _level) return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{tag}] {msg}";
        try { _writer?.WriteLine(line); _writer?.Flush(); } catch { }
        if (_echo) Console.WriteLine(min == LogLevel.Normal ? msg : line);
    }

    public void Info(string m) => Write(LogLevel.Normal, "INFO", m);
    public void Verbose(string m) => Write(LogLevel.Verbose, "VERB", m);
    public void Debug(string m) => Write(LogLevel.Debug, "DBUG", m);
    public void Warn(string m) { HadWarnings = true; Write(LogLevel.Normal, "WARN", m); }

    public void Dispose() { try { _writer?.Dispose(); } catch { } _writer = null; }
}
