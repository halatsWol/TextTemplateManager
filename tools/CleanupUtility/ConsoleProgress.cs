namespace TtmCleanup;

/// <summary>A compact console progress bar for /nonewwindow mode — an in-place animated bar on a real
/// console, or one clean line per phase when stdout is redirected/piped. The detailed log goes to the
/// file, never here.</summary>
internal sealed class ConsoleProgress
{
    private const int Total = 5;   // Locating, Closing, Files, Registry, Data
    private int _step;
    private bool _wroteBar;
    private readonly bool _redirected;

    public ConsoleProgress()
    {
        try { _redirected = Console.IsOutputRedirected; } catch { _redirected = true; }
    }

    public void Report(string msg)
    {
        _step = Math.Min(_step + 1, Total);
        if (_redirected) { Console.WriteLine(msg); return; }
        int pct = _step * 100 / Total;
        int fill = _step * 20 / Total;
        string bar = new string('#', fill) + new string('-', 20 - fill);
        try { Console.Write($"\r[{bar}] {pct,3}%  {Trunc(msg, 45),-45}"); _wroteBar = true; } catch { }
    }

    public void Finish()
    {
        if (_redirected || !_wroteBar) return;
        try { Console.Write("\r" + new string(' ', 78) + "\r"); } catch { }   // clear the bar line
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
