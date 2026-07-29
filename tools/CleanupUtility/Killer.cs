using System.Diagnostics;

namespace TtmCleanup;

/// <summary>Frees file locks before deletion: force-kills ttm.exe (with its WebView2 child tree) and,
/// unless told otherwise, restarts TextInputHost.exe (which Windows respawns) since it can hold handles
/// into a WinUI/WebView2 app.</summary>
internal static class Killer
{
    public static void Run(UserContext ctx, bool keepTextInputHost, Logger log)
    {
        // Only filter by session for the current user; for another user we can't cheaply know their
        // session, so we act on all sessions (best effort — usually they're logged off with nothing running).
        uint? session = null;
        if (ctx.IsCurrentUser)
        {
            Native.ProcessIdToSessionId(Native.GetCurrentProcessId(), out uint mine);
            session = mine;
        }

        int ttm = KillByName("ttm", session, tree: true, log);
        log.Info(ttm > 0 ? $"closed {ttm} running app process(es)." : "no running app process found.");

        // Belt-and-braces: any WebView2 host still up (orphaned) — kill those too.
        int wv = KillByName("msedgewebview2", session, tree: false, log, onlyIfTtmGone: true);
        if (wv > 0) log.Verbose($"closed {wv} lingering WebView2 host process(es).");

        if (!keepTextInputHost)
        {
            int t = KillByName("TextInputHost", session, tree: false, log);
            log.Verbose(t > 0 ? $"restarted TextInputHost ({t} killed; Windows respawns it)." : "TextInputHost not running.");
        }

        Thread.Sleep(400);   // let handles release before we start deleting
    }

    private static int KillByName(string name, uint? session, bool tree, Logger log, bool onlyIfTtmGone = false)
    {
        int count = 0;
        Process[] procs;
        try { procs = Process.GetProcessesByName(name); } catch { return 0; }
        foreach (var p in procs)
        {
            try
            {
                if (session is uint s)
                {
                    if (!Native.ProcessIdToSessionId((uint)p.Id, out uint ps) || ps != s) { p.Dispose(); continue; }
                }
                p.Kill(entireProcessTree: tree);
                p.WaitForExit(3000);
                count++;
            }
            catch (Exception ex) { log.Debug($"could not kill {name} pid {p.Id}: {ex.Message}"); }
            finally { p.Dispose(); }
        }
        return count;
    }
}
