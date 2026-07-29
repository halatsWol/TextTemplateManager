namespace TtmCleanup;

internal enum LogLevel { Normal, Verbose, Debug }

/// <summary>Parsed command-line options. Flags accept /x, -x or --x; valued flags take :value or =value.</summary>
internal sealed class Options
{
    public bool NoArgs;               // launched with zero args -> GUI mode
    public bool ShowHelp;

    public bool Force;                // skip confirmation
    public bool WhatIf;               // simulate; change nothing
    public bool Quiet;                // no window, no console progress; log + errors only
    public bool NoNewWindow;          // no GUI window; progress + prompt in the console
    public bool KeepTextInputHost;    // don't restart TextInputHost

    public string? User;              // clean another user's install (needs elevation)
    public string? LogPath;           // file or folder; folder => default filename inside it
    public LogLevel LogLevel = LogLevel.Normal;

    public bool RemoveSettings;
    public bool RemoveSync;
    public bool RemoveTemplates;
    public bool RemoveAllData;        // implies the three above

    public readonly List<string> Errors = new();

    public bool AnyDataSelected => RemoveSettings || RemoveSync || RemoveTemplates || RemoveAllData;

    /// <summary>Human-readable summary of the effective options, for the log.</summary>
    public string Describe()
    {
        var p = new List<string> { "target=" + (User ?? "current user") };
        if (Force) p.Add("force");
        if (WhatIf) p.Add("whatif");
        if (Quiet) p.Add("quiet");
        if (NoNewWindow) p.Add("nonewwindow");
        if (KeepTextInputHost) p.Add("keepTextInputHost");
        p.Add("loglevel=" + LogLevel.ToString().ToLowerInvariant());
        if (LogPath != null) p.Add("log=" + LogPath);
        string data = RemoveAllData ? "all"
            : string.Join("+", new[] {
                RemoveSettings ? "settings" : null,
                RemoveSync ? "sync" : null,
                RemoveTemplates ? "templates" : null }.Where(x => x != null));
        p.Add("data=" + (string.IsNullOrEmpty(data) ? "none" : data));
        return string.Join(", ", p);
    }

    public static Options Parse(string[] args)
    {
        var o = new Options { NoArgs = args.Length == 0 };

        foreach (var raw in args)
        {
            // Split flag / value on the first ':' or '='.
            string a = raw;
            string? val = null;
            int sep = a.IndexOfAny(new[] { ':', '=' });
            if (sep >= 0) { val = a[(sep + 1)..]; a = a[..sep]; }

            string name = a.TrimStart('/', '-').ToLowerInvariant();

            switch (name)
            {
                case "?" or "h" or "help": o.ShowHelp = true; break;
                case "force" or "y" or "yes": o.Force = true; break;
                case "whatif" or "dryrun" or "dry-run": o.WhatIf = true; break;
                case "quiet" or "q": o.Quiet = true; break;
                case "nonewwindow" or "no-window" or "nowindow": o.NoNewWindow = true; break;
                case "keeptextinputhost" or "keep-textinputhost": o.KeepTextInputHost = true; break;

                case "removesettings" or "remove-settings": o.RemoveSettings = true; break;
                case "removesync" or "remove-sync": o.RemoveSync = true; break;
                case "removetemplates" or "remove-templates" or "removedata": o.RemoveTemplates = true; break;
                case "removeall" or "remove-all" or "removealldata" or "remove-all-data": o.RemoveAllData = true; break;

                case "user":
                    if (string.IsNullOrWhiteSpace(val)) o.Errors.Add("/user requires a value, e.g. /user:jdoe");
                    else o.User = val.Trim();
                    break;
                case "log":
                    if (string.IsNullOrWhiteSpace(val)) o.Errors.Add("/log requires a path, e.g. /log:C:\\Temp\\clean.log");
                    else o.LogPath = val.Trim().Trim('"');
                    break;
                case "loglevel" or "logdetail":
                    switch ((val ?? "").Trim().ToLowerInvariant())
                    {
                        case "normal" or "": o.LogLevel = LogLevel.Normal; break;
                        case "verbose": o.LogLevel = LogLevel.Verbose; break;
                        case "debug": o.LogLevel = LogLevel.Debug; break;
                        default: o.Errors.Add($"unknown /loglevel '{val}' (use normal, verbose or debug)"); break;
                    }
                    break;

                default:
                    o.Errors.Add($"unknown option '{raw}'");
                    break;
            }
        }

        // "Everything" implies the granular ones.
        if (o.RemoveAllData) { o.RemoveSettings = o.RemoveSync = o.RemoveTemplates = true; }
        return o;
    }

    public const string HelpText = """
    TextTemplateManager Cleanup Utility

    Completely removes Text Template Manager without using its uninstaller: kills the app, clears
    the install folder, shortcuts, autostart and registry entries (including the older mis-registered
    AppId), and — only if you ask — the user data. Run it with no arguments for a small window.

    Usage:
      TextTemplateManager-CleanupUtility.exe [options]

    General:
      /force                 Proceed without the confirmation prompt.
      /whatif                Show everything that would be done, but change nothing.
      /quiet                 No window and no console progress (log + errors only). Needs /force.
      /nonewwindow           No GUI window; show progress and the prompt in the console.
      /keeptextinputhost     Do not restart TextInputHost.exe.
      /?  /h  /help          Show this help.

    User data (opt-in; default is to keep all data):
      /removesettings        Delete settings.ttmsettings.
      /removesync            Delete sync.ttmsettings.
      /removetemplates       Delete data.ttmdata (your templates).
      /removeall             Delete the entire app data folder (all of the above).

    Target another user (requires running as administrator):
      /user:<name>           Clean the install for another local user.

    Logging:
      /log:<file|folder>     Log file path; a folder (or trailing \) uses a default filename inside it.
                             Missing folders are created; falls back to the default location, then TEMP.
      /loglevel:<level>      normal (default) | verbose | debug.

    Flags accept /x, -x or --x. Examples:
      TextTemplateManager-CleanupUtility.exe
      TextTemplateManager-CleanupUtility.exe /force /removeall
      TextTemplateManager-CleanupUtility.exe /force /quiet /log:C:\Temp\ /loglevel:verbose
      TextTemplateManager-CleanupUtility.exe /user:jdoe /force   (elevated)
    """;
}
