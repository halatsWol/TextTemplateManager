using System.Runtime.InteropServices;

namespace TtmCleanup;

/// <summary>Win32 P/Invoke surface. Kept deliberately small — only what the tool actually calls.</summary>
internal static unsafe class Native
{
    // ---- Console / session ----
    [DllImport("kernel32.dll")] public static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AllocConsole();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] public static extern uint GetConsoleProcessList(uint[] list, uint count);
    [DllImport("kernel32.dll")] public static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    // ---- Window show/hide ----
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hWnd, int nCmdShow);
    public const int SW_HIDE = 0, SW_SHOW = 5, SW_RESTORE = 9;

    // ---- Message box ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
    public const uint MB_OK = 0x0, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONWARNING = 0x30,
                      MB_ICONINFORMATION = 0x40, MB_ICONQUESTION = 0x20, MB_IDYES = 6, MB_SETFOREGROUND = 0x10000,
                      MB_TOPMOST = 0x40000;

    // ---- Toolhelp process snapshot (parent-PID lookup for WebView2 children) ----
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32FirstW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32NextW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(nint h);
    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    // ---- Registry hive load/unload (cleaning an offline user's NTUSER.DAT) ----
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegLoadKeyW(nint hKey, string lpSubKey, string lpFile);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegUnLoadKeyW(nint hKey, string lpSubKey);
    public static readonly nint HKEY_USERS = unchecked((nint)0x80000003);

    // ---- Privilege enable (SeBackup/SeRestore for RegLoadKey) ----
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(nint h, uint access, out nint token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool LookupPrivilegeValueW(string? host, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, nint prev, nint retLen);
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8;
    public const uint SE_PRIVILEGE_ENABLED = 0x2;

    // Pack=4 so the 8-byte LUID aligns to 4 (matching Win32's DWORD count + LUID + DWORD attributes = 16 bytes).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public long Luid; public uint Attributes; }

    // ---- Shell association-change notification ----
    [DllImport("shell32.dll")] public static extern void SHChangeNotify(int wEventId, uint uFlags, nint a, nint b);
    public const int SHCNE_ASSOCCHANGED = 0x08000000;
    public const uint SHCNF_IDLIST = 0x0000;

    // ---- Window class / message loop (the minimal GUI) ----
    public delegate nint WndProcManaged(nint h, uint msg, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowExW(uint exStyle, string? cls, string? name, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint DefWindowProcW(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMessageW(out MSG m, nint h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint DispatchMessageW(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint SendMessageW(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PostMessageW(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern bool EnableWindow(nint h, bool enable);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetWindowTextW(nint h, string s);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool MoveWindow(nint h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint LoadCursorW(nint inst, nint name);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint h);
    [DllImport("gdi32.dll")] public static extern nint GetStockObject(int obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern nint CreateFontW(int height, int width, int esc, int orient, int weight,
        uint italic, uint underline, uint strikeout, uint charset, uint outPrec, uint clipPrec,
        uint quality, uint pitchAndFamily, string faceName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("comctl32.dll")] public static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

    [StructLayout(LayoutKind.Sequential)] public struct INITCOMMONCONTROLSEX { public uint dwSize; public uint dwICC; }

    // Window / control styles & messages used by the GUI.
    public const uint WS_OVERLAPPED = 0x0, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000,
        WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000, WS_TABSTOP = 0x00010000, WS_GROUP = 0x00020000,
        WS_EX_DLGMODALFRAME = 0x00000001, WS_EX_TOPMOST = 0x00000008, WS_EX_COMPOSITED = 0x02000000,
        WS_CLIPCHILDREN = 0x02000000;
    public const uint SS_LEFT = 0x0;
    public const uint BS_AUTOCHECKBOX = 0x3, BS_PUSHBUTTON = 0x0, BS_DEFPUSHBUTTON = 0x1;
    public const uint BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1;
    public const nint BST_CHECKED = 1, BST_UNCHECKED = 0;
    public const uint WM_COMMAND = 0x0111, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_SETFONT = 0x0030, WM_APP = 0x8000;
    public const int IDC_ARROW = 32512, DEFAULT_GUI_FONT = 17, COLOR_BTNFACE = 15;
    public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    public const uint CS_HREDRAW = 0x2, CS_VREDRAW = 0x1;
    public const uint ICC_STANDARD_CLASSES = 0x00004000, ICC_PROGRESS_CLASS = 0x00000020;
    public const uint WM_USER = 0x0400, PBM_SETMARQUEE = WM_USER + 10;
    public const uint PBS_MARQUEE = 0x08;
}
