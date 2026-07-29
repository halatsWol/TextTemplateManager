namespace TtmCleanup;

internal static class Program
{
    private const int OK = 0, CANCELLED = 1, BAD_ARGS = 2, NEEDS_FORCE = 3, PARTIAL = 4, NEEDS_ADMIN = 5, USER_ERROR = 6;

    private static int Main(string[] rawArgs)
    {
        var args = rawArgs;
        int? parentPid = Relocator.ExtractParentPid(ref args);   // are we the relocated child?
        var opts = Options.Parse(args);

        bool relocatedChild = parentPid != null;
        SetupConsole();

        if (opts.ShowHelp) { Notify(Options.HelpText, false); return OK; }
        if (opts.Errors.Count > 0)
        {
            Notify("Invalid arguments:\r\n  " + string.Join("\r\n  ", opts.Errors) + "\r\n\r\nUse /? for help.", true);
            return BAD_ARGS;
        }

        if (parentPid is int pp) Relocator.WaitForParent(pp);

        // Relocate out of the install folder before deleting it (current-user clean, bundled copy only).
        if (!relocatedChild && opts.User == null && Relocator.RunningInsideCurrentInstall())
        {
            if (Relocator.RelaunchFromTemp(args)) return OK;   // child continues the work
        }

        // Elevation gate for cross-user cleanup.
        if (opts.User != null && !Session.Elevated)
        {
            Notify("Cleaning another user's installation requires running as administrator.", true);
            return NEEDS_ADMIN;
        }

        // Logger (default location: current user's ...\Programs\Marflow Software).
        string defaultLogDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Marflow Software");
        var tagParts = new List<string>();
        if (!string.IsNullOrEmpty(opts.User)) tagParts.Add(Sanitize(opts.User!));
        if (opts.WhatIf) tagParts.Add("whatif");   // mark dry-run logs in the filename
        string tag = string.Join("-", tagParts);
        // The log always goes to the file only; console feedback is the progress bar (see below).
        using var log = new Logger(opts.LogPath, defaultLogDir, tag, opts.LogLevel, echoConsole: false);
        log.Info("TextTemplateManager cleanup starting.");
        if (log.FilePath != null) log.Verbose("log file: " + log.FilePath);
        log.Debug($"env: elevated={Session.Elevated}, interactiveDesktop={Session.InteractiveDesktop}, "
                + $"canWriteConsole={_canWriteConsole}, consoleHidden={_consoleHidden}, promptConsole={CanPromptConsole()}, relocatedChild={relocatedChild}");

        // Resolve the target user.
        UserContext? ctx;
        if (opts.User == null) ctx = UserContext.Current();
        else
        {
            ctx = UserContext.ForUser(opts.User, log, out string err);
            if (ctx == null) { log.Warn(err); Notify(err, true); return USER_ERROR; }
        }

        using (ctx)
        {
            var plan = new Plan(ctx);
            CleanupResult DoWork(Options o, Action<string> progress) => Runner.Execute(ctx, plan, o, log, progress);

            bool guiSelect = opts.NoArgs;
            bool useWindow = guiSelect || (!opts.Quiet && !opts.NoNewWindow);

            // Confirmation (GUI select uses its own Clean-up button).
            if (!guiSelect && !opts.Force && !opts.WhatIf)
            {
                string prompt = ConfirmText(opts, ctx);
                bool ok;
                if (CanPromptConsole()) ok = ConsoleConfirm(prompt);
                else if (Session.InteractiveDesktop) ok = Gui.ConfirmBox(prompt);
                else
                {
                    Notify("This permanently removes Text Template Manager. Re-run with /force "
                           + "(there is no interactive session to confirm).", true);
                    return NEEDS_FORCE;
                }
                if (!ok) { log.Info("cancelled by user."); return CANCELLED; }
            }

            // Run.
            CleanupResult? res;
            if (guiSelect) res = Gui.SelectAndRun(opts, DoWork);
            else if (useWindow) res = Gui.RunWithProgress(opts, DoWork);
            else
            {
                // /nonewwindow (or headless): a console progress bar, not a log dump. /quiet stays silent.
                var cp = (!opts.Quiet && _canWriteConsole) ? new ConsoleProgress() : null;
                Action<string> prog = cp != null ? cp.Report : _ => { };
                res = DoWork(opts, prog);
                cp?.Finish();
            }

            if (relocatedChild) Relocator.ScheduleSelfDelete();

            if (res == null) { log.Info("cancelled by user."); return CANCELLED; }

            string msg = res.WhatIf
                ? $"WhatIf: {res.Removed} item(s) would be removed. Nothing was changed."
                : res.NeedsRestart
                    ? "Cleanup finished, but some files were in use and could not be removed.\r\n"
                      + "Please restart your device and run this tool again."
                    : $"Cleanup successful. {res.Removed} item(s) removed.";
            log.Info(msg.Replace("\r\n", " "));
            if (useWindow) Gui.ResultBox(msg, res.NeedsRestart);
            if (_canWriteConsole && !opts.Quiet) Console.WriteLine(msg);

            return res.NeedsRestart ? PARTIAL : OK;
        }
    }

    private static string ConfirmText(Options o, UserContext ctx)
    {
        string who = o.User == null ? "" : $" for user '{ctx.UserName}'";
        string data = o.AnyDataSelected
            ? (o.RemoveAllData ? " and ALL user data" : " and the selected user data")
            : " (user data will be kept)";
        return $"Completely remove Text Template Manager{who}{data}?";
    }

    private static bool ConsoleConfirm(string prompt)
    {
        try
        {
            Console.Write(prompt + " [y/N]: ");
            string? line = Console.ReadLine()?.Trim();
            return string.Equals(line, "y", StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, "yes", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool _canWriteConsole, _consoleHidden;

    // Hide only a real, owned, non-redirected console (a double-click console window); leave a shell's or a
    // redirected/piped stdout intact so output still flows.
    private static void SetupConsole()
    {
        bool redirected; try { redirected = Console.IsOutputRedirected; } catch { redirected = false; }
        nint win = Native.GetConsoleWindow();
        bool own = Native.GetConsoleProcessList(new uint[4], 4) <= 1;
        if (own && win != nint.Zero && !redirected) { Native.ShowWindow(win, Native.SW_HIDE); _consoleHidden = true; }
        _canWriteConsole = redirected || (win != nint.Zero && !_consoleHidden);
    }

    private static bool CanPromptConsole()
    {
        if (_consoleHidden || Native.GetConsoleWindow() == nint.Zero) return false;
        try { return !Console.IsInputRedirected; } catch { return false; }
    }

    // Print to the console when one is available; otherwise fall back to a message box.
    private static void Notify(string text, bool error)
    {
        if (_canWriteConsole)
        {
            if (error) Console.Error.WriteLine(text); else Console.WriteLine(text);
        }
        else
        {
            Native.MessageBoxW(nint.Zero, text, "Text Template Manager — Cleanup",
                (error ? Native.MB_ICONERROR : Native.MB_ICONINFORMATION) | Native.MB_SETFOREGROUND | Native.MB_TOPMOST);
        }
    }

    private static string Sanitize(string s)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
