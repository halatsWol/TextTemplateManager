using System.Runtime.InteropServices;

namespace TtmCleanup;

/// <summary>The minimal Win32 GUI: a checkbox selection window (no-args mode) and a progress-only window
/// (CLI default). Deliberately hand-rolled so the tool stays a tiny, dependency-free NativeAOT exe.</summary>
internal static unsafe class Gui
{
    private const string ClassName = "TtmCleanupWnd";
    private const int ID_SETTINGS = 101, ID_SYNC = 102, ID_TEMPLATES = 103, ID_ALL = 104, ID_CLEAN = 105, ID_CANCEL = 106;
    private const uint WM_DONE = Native.WM_APP;          // worker finished
    private const uint WM_START = Native.WM_APP + 1;     // begin work (progress-only mode)

    private static nint _hwnd, _cbS, _cbSy, _cbT, _cbA, _btnClean, _lblStatus, _bar;
    private static bool _showChecks, _cancelled, _started, _iccDone;
    private static Options _opts = null!;
    private static Func<Options, Action<string>, CleanupResult> _work = null!;
    private static CleanupResult? _result;
    private static ushort _atom;
    private static nint _hInst, _hFont;
    private static int _scale = 100;

    public static CleanupResult? SelectAndRun(Options o, Func<Options, Action<string>, CleanupResult> w) => Run(true, o, w);
    public static CleanupResult? RunWithProgress(Options o, Func<Options, Action<string>, CleanupResult> w) => Run(false, o, w);

    private static int S(int v) => v * _scale / 100;

    private static CleanupResult? Run(bool showChecks, Options o, Func<Options, Action<string>, CleanupResult> w)
    {
        _showChecks = showChecks; _opts = o; _work = w; _result = null; _cancelled = false; _started = false;
        _hInst = Native.GetModuleHandleW(null);
        EnsureIcc();
        EnsureClass();

        int cx = Native.GetSystemMetrics(Native.SM_CXSCREEN), cy = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        _hwnd = Native.CreateWindowExW(0, ClassName, "Text Template Manager — Cleanup",
            Native.WS_CAPTION | Native.WS_SYSMENU | Native.WS_CLIPCHILDREN,
            cx / 2 - 240, cy / 2 - 170, 480, 340, nint.Zero, nint.Zero, _hInst, nint.Zero);
        if (_hwnd == nint.Zero) return null;

        uint dpi = 96;
        try { dpi = Native.GetDpiForWindow(_hwnd); if (dpi < 96) dpi = 96; } catch { }
        _scale = (int)(dpi * 100 / 96);
        // Segoe UI 9pt scaled to this window's DPI (DEFAULT_GUI_FONT is a chunky, non-scaling bitmap font).
        _hFont = Native.CreateFontW(-(int)(9 * dpi / 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (_hFont == nint.Zero) _hFont = Native.GetStockObject(Native.DEFAULT_GUI_FONT);

        BuildControls();
        int w2 = S(showChecks ? 460 : 400), h2 = S(showChecks ? 320 : 150);
        Native.MoveWindow(_hwnd, cx / 2 - w2 / 2, cy / 2 - h2 / 2, w2, h2, true);
        Native.ShowWindow(_hwnd, Native.SW_SHOW);
        Native.SetForegroundWindow(_hwnd);

        if (!showChecks) Native.PostMessageW(_hwnd, WM_START, nint.Zero, nint.Zero);

        while (Native.GetMessageW(out Native.MSG m, nint.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref m);
            Native.DispatchMessageW(ref m);
        }
        return _cancelled ? null : _result;
    }

    private static nint Child(string cls, string text, uint style, int x, int y, int w, int h, int id)
    {
        var c = Native.CreateWindowExW(0, cls, text, Native.WS_CHILD | Native.WS_VISIBLE | style,
            S(x), S(y), S(w), S(h), _hwnd, id, _hInst, nint.Zero);
        Native.SendMessageW(c, Native.WM_SETFONT, _hFont, 1);
        return c;
    }

    private static void BuildControls()
    {
        if (_showChecks)
        {
            Child("STATIC",
                "Completely removes Text Template Manager: the app, shortcuts, autostart and registry entries.\r\n\r\nOptionally also delete your data (off by default):",
                Native.SS_LEFT, 15, 12, 430, 60, 0);
            _cbS = Child("BUTTON", "Delete settings", Native.BS_AUTOCHECKBOX | Native.WS_TABSTOP, 20, 78, 200, 22, ID_SETTINGS);
            _cbSy = Child("BUTTON", "Delete sync configuration", Native.BS_AUTOCHECKBOX | Native.WS_TABSTOP, 20, 102, 250, 22, ID_SYNC);
            _cbT = Child("BUTTON", "Delete templates", Native.BS_AUTOCHECKBOX | Native.WS_TABSTOP, 20, 126, 200, 22, ID_TEMPLATES);
            _cbA = Child("BUTTON", "Delete everything (all app data)", Native.BS_AUTOCHECKBOX | Native.WS_TABSTOP, 20, 150, 280, 22, ID_ALL);
            if (_opts.RemoveSettings) Native.SendMessageW(_cbS, Native.BM_SETCHECK, Native.BST_CHECKED, 0);
            if (_opts.RemoveSync) Native.SendMessageW(_cbSy, Native.BM_SETCHECK, Native.BST_CHECKED, 0);
            if (_opts.RemoveTemplates) Native.SendMessageW(_cbT, Native.BM_SETCHECK, Native.BST_CHECKED, 0);
            if (_opts.RemoveAllData) { Native.SendMessageW(_cbA, Native.BM_SETCHECK, Native.BST_CHECKED, 0); ApplyEverything(true); }

            _lblStatus = Child("STATIC", "", Native.SS_LEFT, 15, 185, 430, 20, 0);
            _bar = Child("msctls_progress32", "", Native.PBS_MARQUEE, 15, 210, 430, 18, 0);
            Native.ShowWindow(_bar, Native.SW_HIDE);   // shown once cleanup starts
            _btnClean = Child("BUTTON", "Clean up", Native.BS_DEFPUSHBUTTON | Native.WS_TABSTOP, 250, 250, 90, 28, ID_CLEAN);
            Child("BUTTON", "Cancel", Native.BS_PUSHBUTTON | Native.WS_TABSTOP, 350, 250, 90, 28, ID_CANCEL);
        }
        else
        {
            Child("STATIC", "Cleaning up Text Template Manager…", Native.SS_LEFT, 15, 18, 370, 22, 0);
            _lblStatus = Child("STATIC", "Working…", Native.SS_LEFT, 15, 46, 370, 20, 0);
            _bar = Child("msctls_progress32", "", Native.PBS_MARQUEE, 15, 74, 370, 18, 0);
        }
    }

    private static void ApplyEverything(bool on)
    {
        if (on)
        {
            Native.SendMessageW(_cbS, Native.BM_SETCHECK, Native.BST_CHECKED, 0);
            Native.SendMessageW(_cbSy, Native.BM_SETCHECK, Native.BST_CHECKED, 0);
            Native.SendMessageW(_cbT, Native.BM_SETCHECK, Native.BST_CHECKED, 0);
        }
        Native.EnableWindow(_cbS, !on);
        Native.EnableWindow(_cbSy, !on);
        Native.EnableWindow(_cbT, !on);
    }

    private static bool Checked(nint cb) => Native.SendMessageW(cb, Native.BM_GETCHECK, 0, 0) == Native.BST_CHECKED;

    private static void StartWork()
    {
        if (_started) return;
        _started = true;
        if (_showChecks)
        {
            Native.EnableWindow(_cbS, false); Native.EnableWindow(_cbSy, false); Native.EnableWindow(_cbT, false);
            Native.EnableWindow(_cbA, false); Native.EnableWindow(_btnClean, false);
        }
        Native.SetWindowTextW(_lblStatus, "Working…");
        if (_bar != nint.Zero)
        {
            Native.ShowWindow(_bar, Native.SW_SHOW);
            Native.SendMessageW(_bar, Native.PBM_SETMARQUEE, 1, 30);   // animate (indeterminate)
        }

        var t = new Thread(() =>
        {
            try { _result = _work(_opts, msg => { try { Native.SetWindowTextW(_lblStatus, msg); } catch { } }); }
            catch { _result = new CleanupResult { Errors = 1 }; }
            Native.PostMessageW(_hwnd, WM_DONE, nint.Zero, nint.Zero);
        }) { IsBackground = true };
        t.Start();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static nint WndProc(nint h, uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case Native.WM_COMMAND:
                int id = (int)(w & 0xFFFF);
                if (id == ID_ALL) ApplyEverything(Checked(_cbA));
                else if (id == ID_CLEAN)
                {
                    _opts.RemoveSettings = Checked(_cbS);
                    _opts.RemoveSync = Checked(_cbSy);
                    _opts.RemoveTemplates = Checked(_cbT);
                    _opts.RemoveAllData = Checked(_cbA);
                    StartWork();
                }
                else if (id == ID_CANCEL) { _cancelled = true; Native.DestroyWindow(h); }
                return nint.Zero;

            case WM_START: StartWork(); return nint.Zero;
            case WM_DONE: Native.DestroyWindow(h); return nint.Zero;
            case Native.WM_CLOSE:
                if (!_started) _cancelled = true;
                Native.DestroyWindow(h); return nint.Zero;
            case Native.WM_DESTROY: Native.PostQuitMessage(0); return nint.Zero;
        }
        return Native.DefWindowProcW(h, msg, w, l);
    }

    private static void EnsureIcc()
    {
        if (_iccDone) return;
        _iccDone = true;
        var icc = new Native.INITCOMMONCONTROLSEX
        {
            dwSize = (uint)Marshal.SizeOf<Native.INITCOMMONCONTROLSEX>(),
            dwICC = Native.ICC_STANDARD_CLASSES | Native.ICC_PROGRESS_CLASS,
        };
        Native.InitCommonControlsEx(ref icc);
    }

    private static void EnsureClass()
    {
        if (_atom != 0) return;
        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            style = Native.CS_HREDRAW | Native.CS_VREDRAW,
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc,
            hInstance = _hInst,
            hCursor = Native.LoadCursorW(nint.Zero, Native.IDC_ARROW),
            hbrBackground = (nint)(Native.COLOR_BTNFACE + 1),
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
        };
        _atom = Native.RegisterClassExW(ref wc);
    }

    // ---- simple modal message boxes for confirmation / results ----
    public static bool ConfirmBox(string text) =>
        Native.MessageBoxW(nint.Zero, text, "Text Template Manager — Cleanup",
            Native.MB_YESNO | Native.MB_ICONQUESTION | Native.MB_SETFOREGROUND | Native.MB_TOPMOST) == (int)Native.MB_IDYES;

    public static void ResultBox(string text, bool warning) =>
        Native.MessageBoxW(nint.Zero, text, "Text Template Manager — Cleanup",
            (warning ? Native.MB_ICONWARNING : Native.MB_ICONINFORMATION) | Native.MB_SETFOREGROUND | Native.MB_TOPMOST);
}
