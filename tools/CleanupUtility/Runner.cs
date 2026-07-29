using Microsoft.Win32;

namespace TtmCleanup;

internal sealed class CleanupResult
{
    public bool WhatIf;
    public int Removed;    // items removed (or, in whatif, that would be removed)
    public int Locked;     // files that could not be deleted (in use)
    public int Errors;     // other failures
    public bool NeedsRestart => Locked > 0;
}

/// <summary>Carries out the plan: reads install locations, frees locks, then deletes files, registry
/// entries and (opt-in) user data — every path checked by <see cref="PathSafety"/> first.</summary>
internal static class Runner
{
    public static CleanupResult Execute(UserContext ctx, Plan plan, Options opts, Logger log, Action<string>? progress)
    {
        var r = new CleanupResult { WhatIf = opts.WhatIf };
        log.Info("options — " + opts.Describe());
        if (opts.WhatIf) log.Info("WHATIF mode — nothing will be changed.");

        // 1) Install directories: whatever the uninstall keys point at (validated), plus the default.
        progress?.Invoke("Locating installation…");
        var dirs = new List<string>();
        foreach (var uk in Plan.UninstallKeys)
        {
            string? loc = null;
            try { using var k = ctx.UserRoot.OpenSubKey(uk); loc = k?.GetValue("InstallLocation") as string; } catch { }
            if (!string.IsNullOrWhiteSpace(loc))
            {
                if (PathSafety.ValidateDir(loc, Plan.InstallLeaf, out string full))
                { if (!dirs.Contains(full, StringComparer.OrdinalIgnoreCase)) dirs.Add(full); }
                else log.Warn($"ignored unsafe InstallLocation from registry: '{loc}'");
            }
        }
        if (PathSafety.ValidateDir(plan.InstallDefault, Plan.InstallLeaf, out string def)
            && !dirs.Contains(def, StringComparer.OrdinalIgnoreCase))
            dirs.Add(def);
        log.Debug($"install locations resolved: {dirs.Count}");
        foreach (var d in dirs) log.Debug("  install dir: " + d);

        // 2) Free locks (real runs only).
        if (!opts.WhatIf) { progress?.Invoke("Closing the app…"); Killer.Run(ctx, opts.KeepTextInputHost, log); }
        else log.Info("[whatif] would force-close ttm.exe (+ WebView2) and restart TextInputHost.");

        // 3) Program files + shortcuts.
        progress?.Invoke("Removing program files…");
        foreach (var d in dirs) RemoveDir(d, Plan.InstallLeaf, r, opts, log);
        RemoveFile(plan.StartMenuLnk, r, opts, log, "Start-menu shortcut");
        RemoveFile(plan.DesktopLnk, r, opts, log, "desktop shortcut");
        RemoveDirIfEmpty(plan.InstallParent, opts, log);

        // 4) Registry.
        progress?.Invoke("Removing registry entries…");
        RemoveRegistry(ctx, r, opts, log);

        // 5) User data (opt-in).
        if (opts.AnyDataSelected)
        {
            progress?.Invoke("Removing user data…");
            RemoveData(ctx, plan, opts, r, log);
        }

        if (!opts.WhatIf) Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, Native.SHCNF_IDLIST, nint.Zero, nint.Zero);
        return r;
    }

    // ---- filesystem ----

    private static void RemoveDir(string path, string leaf, CleanupResult r, Options opts, Logger log)
    {
        if (!PathSafety.ValidateDir(path, leaf, out string full)) { log.Warn($"skipped unsafe path: '{path}'"); return; }
        log.Debug($"path validated for deletion: {full}");
        if (!Directory.Exists(full)) { log.Verbose($"not present: {full}"); return; }

        if (opts.WhatIf)
        {
            log.Info($"[whatif] would remove folder: {full}");
            try { foreach (var f in Directory.GetFiles(full, "*", SearchOption.AllDirectories)) log.Debug("  would delete: " + f); }
            catch (Exception ex) { log.Debug($"  could not enumerate {full}: {ex.Message}"); }
            r.Removed++; return;
        }

        int locked = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            locked = DeleteTree(full, log);
            if (!Directory.Exists(full)) break;
            Thread.Sleep(300);
        }
        if (!Directory.Exists(full) && locked == 0) { log.Info($"removed folder: {full}"); r.Removed++; }
        else { r.Locked += Math.Max(locked, 1); log.Warn($"could not fully remove (files in use): {full}"); }
    }

    private static int DeleteTree(string dir, Logger log)
    {
        int locked = 0;
        string[] files;
        try { files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories); } catch { return 1; }
        foreach (var f in files)
        {
            try
            {
                log.Debug("deleting: " + f);
                var a = File.GetAttributes(f);
                if (a.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(f, a & ~FileAttributes.ReadOnly);
                File.Delete(f);
            }
            catch (Exception ex) { locked++; log.Verbose($"locked file: {f} ({ex.GetType().Name})"); }
        }
        try
        {
            foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories)
                                       .OrderByDescending(x => x.Length))
                try { Directory.Delete(d, false); } catch { }
            Directory.Delete(dir, false);
        }
        catch { }
        return locked;
    }

    private static void RemoveFile(string path, CleanupResult r, Options opts, Logger log, string what)
    {
        try
        {
            if (!File.Exists(path)) { log.Verbose($"not present: {what} ({path})"); return; }
            if (opts.WhatIf) { log.Info($"[whatif] would remove {what}: {path}"); r.Removed++; return; }
            var a = File.GetAttributes(path);
            if (a.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            File.Delete(path);
            log.Info($"removed {what}: {path}"); r.Removed++;
        }
        catch (Exception ex) { r.Locked++; log.Warn($"could not remove {what} ({path}): {ex.Message}"); }
    }

    private static void RemoveDirIfEmpty(string path, Options opts, Logger log)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            if (Directory.EnumerateFileSystemEntries(path).Any()) return;
            if (opts.WhatIf) { log.Info($"[whatif] would remove empty folder: {path}"); return; }
            Directory.Delete(path, false);
            log.Verbose($"removed empty folder: {path}");
        }
        catch { }
    }

    // ---- registry ----

    private static void RemoveRegistry(UserContext ctx, CleanupResult r, Options opts, Logger log)
    {
        foreach (var uk in Plan.UninstallKeys) DeleteKeyTree(ctx, uk, r, opts, log, "uninstall entry");
        DeleteValue(ctx, Plan.RunKey, Plan.ValueName, r, opts, log, "autostart (Run)");
        DeleteValue(ctx, Plan.ApprovedKey, Plan.ValueName, r, opts, log, "autostart (StartupApproved)");
        DeleteKeyTree(ctx, Plan.ClassesKey + "\\" + Plan.ProgId, r, opts, log, "file type");

        // Remove the .ttmdata extension mapping only if it still points at us.
        try
        {
            using var ext = ctx.UserRoot.OpenSubKey(Plan.ClassesKey + "\\" + Plan.Ext, writable: false);
            string? cur = ext?.GetValue("") as string;
            if (cur == Plan.ProgId) DeleteKeyTree(ctx, Plan.ClassesKey + "\\" + Plan.Ext, r, opts, log, "file association");
            else if (cur != null) log.Info($"left {Plan.Ext} association — now owned by '{cur}', not us.");
        }
        catch (Exception ex) { log.Debug($".ttmdata check failed: {ex.Message}"); }
    }

    private static void DeleteKeyTree(UserContext ctx, string path, CleanupResult r, Options opts, Logger log, string what)
    {
        try
        {
            bool exists; using (var k = ctx.UserRoot.OpenSubKey(path)) exists = k != null;
            if (!exists) { log.Verbose($"not present (reg): {path}"); return; }
            if (opts.WhatIf) { log.Info($"[whatif] would remove registry {what}: {path}"); r.Removed++; return; }
            ctx.UserRoot.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            log.Info($"removed registry {what}: {path}"); r.Removed++;
        }
        catch (Exception ex) { r.Errors++; log.Warn($"could not remove registry {what} ({path}): {ex.Message}"); }
    }

    private static void DeleteValue(UserContext ctx, string keyPath, string name, CleanupResult r, Options opts, Logger log, string what)
    {
        try
        {
            using var k = ctx.UserRoot.OpenSubKey(keyPath, writable: true);
            if (k?.GetValue(name) == null) { log.Verbose($"not present (reg value): {keyPath}\\{name}"); return; }
            if (opts.WhatIf) { log.Info($"[whatif] would remove registry {what}: {keyPath}\\{name}"); r.Removed++; return; }
            k.DeleteValue(name, throwOnMissingValue: false);
            log.Info($"removed registry {what}: {keyPath}\\{name}"); r.Removed++;
        }
        catch (Exception ex) { r.Errors++; log.Warn($"could not remove registry {what}: {ex.Message}"); }
    }

    // ---- user data (opt-in) ----

    private static void RemoveData(UserContext ctx, Plan plan, Options opts, CleanupResult r, Logger log)
    {
        if (opts.RemoveAllData)
        {
            RemoveDir(plan.DataDir, Plan.DataLeaf, r, opts, log);
            RemoveDir(plan.LegacyDataDir, Plan.LegacyDataLeaf, r, opts, log);
            RemoveDirIfEmpty(Path.Combine(ctx.LocalAppData, "Marflow Software"), opts, log);
            return;
        }
        // Granular: only delete inside a validated data folder.
        if (!PathSafety.ValidateDir(plan.DataDir, Plan.DataLeaf, out _))
        { log.Warn("skipped data files — data folder failed the safety check."); return; }

        if (opts.RemoveSettings) RemoveFile(plan.SettingsFile, r, opts, log, "settings");
        if (opts.RemoveSync) RemoveFile(plan.SyncFile, r, opts, log, "sync config");
        if (opts.RemoveTemplates) RemoveFile(plan.TemplatesFile, r, opts, log, "templates");
    }
}
