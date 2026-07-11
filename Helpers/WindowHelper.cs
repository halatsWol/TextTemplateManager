using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TextTemplateManager.Helpers
{
    public static class WindowHelper
    {
        public static CaretContext? TargetCaret { get; private set; }
        public static IntPtr TargetWindowHandle { get; set; }

        public static void CaptureTargetContext()
        {
            TargetWindowHandle = GetForegroundWindow();
            TargetCaret = CaretContext.Capture(TargetWindowHandle);
        }

        // --- Helper Methods for Tracing ---
        public static IntPtr GetTargetWindow() => TargetWindowHandle;
        public static uint GetCurrentPID() => (uint)Environment.ProcessId;
        public static uint GetWindowPID(IntPtr hWnd)
        {
            _ = GetWindowThreadProcessId(hWnd, out uint pid);
            return pid;
        }
        public static string GetWindowTitle(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            return sb.ToString();
        }

        #region Win32 API Imports

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        // --- Restored for App.xaml.cs ---
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        #endregion

        #region Constants & Delegates

        public const int GWL_WNDPROC = -4;
        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public const int SW_RESTORE = 9;
        public const int HWND_TOPMOST = -1;
        public const int HWND_NOTOPMOST = -2;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_SHOWWINDOW = 0x0040;

        #endregion

        #region INPUT Structs
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        #endregion

        public static void ResetModifiers()
        {
            // High-level "Up" events for the usual suspects
            // 0x11 = Ctrl, 0x12 = Alt, 0x10 = Shift, 0x5B/0x5C = Windows Keys
            byte[] keys = { 0x11, 0x12, 0x10, 0x5B, 0x5C };

            foreach (var key in keys)
            {
                // We send a KEYEVENTF_KEYUP (0x0002)
                keybd_event(key, 0, 0x0002, UIntPtr.Zero);
            }
        }

        // ---- Minimum window size (WM_GETMINMAXINFO subclass) ----
        // WinUI 3 has no min-size API, so we subclass the top-level HWND and clamp the min
        // track size. Sizes are given in DIPs and scaled by the window's DPI. A single shared
        // hook services every subclassed window; per-window state (original proc + min dims)
        // is looked up by HWND so multiple windows can each have their own minimum.

        private const uint WM_GETMINMAXINFO = 0x0024;
        private const uint WM_SYSCHAR = 0x0106;
        private static WndProcDelegate? _subclassHook;   // shared hook, kept alive to avoid GC
        private static readonly Dictionary<IntPtr, WindowHook> _hookedWindows = new();

        private sealed class WindowHook
        {
            public IntPtr Orig;
            public int MinW;
            public int MinH;
            public bool SuppressAltBeep;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public int reservedX, reservedY;
            public int maxSizeX, maxSizeY;
            public int maxPositionX, maxPositionY;
            public int minTrackX, minTrackY;
            public int maxTrackX, maxTrackY;
        }

        private static WindowHook EnsureHook(IntPtr hWnd)
        {
            if (_hookedWindows.TryGetValue(hWnd, out var existing)) return existing;
            _subclassHook ??= WindowSubclassProc;
            IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(_subclassHook);
            IntPtr orig = SetWindowLongPtr(hWnd, GWL_WNDPROC, hookPtr);
            var hook = new WindowHook { Orig = orig };
            _hookedWindows[hWnd] = hook;
            return hook;
        }

        /// <summary>Enforces a minimum window size (in DIPs) by subclassing the window proc.</summary>
        public static void SetWindowMinSize(IntPtr hWnd, int minWidthDip, int minHeightDip)
        {
            var hook = EnsureHook(hWnd);
            hook.MinW = minWidthDip;
            hook.MinH = minHeightDip;
        }

        /// <summary>Swallows the "ding" Windows plays for an Alt+key that matches no menu mnemonic
        /// (WM_SYSCHAR). Enable only for windows with no menu bar, or Alt-mnemonics break.</summary>
        public static void SuppressAltMenuBeep(IntPtr hWnd, bool suppress = true)
        {
            EnsureHook(hWnd).SuppressAltBeep = suppress;
        }

        /// <summary>Finds the WinUI content-input child window (which actually receives keyboard
        /// WM_SYSCHAR — the top-level frame does not) and suppresses its Alt+key beep there.
        /// Returns the child HWND, or Zero if the content island isn't realized yet.</summary>
        public static IntPtr SuppressAltMenuBeepOnChild(IntPtr topHwnd)
        {
            IntPtr child = FindDescendantByClass(topHwnd, "InputSite");
            if (child != IntPtr.Zero) SuppressAltMenuBeep(child);
            return child;
        }

        private static IntPtr FindDescendantByClass(IntPtr parent, string classContains)
        {
            IntPtr result = IntPtr.Zero;
            EnumChildProc cb = (h, l) =>
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString().IndexOf(classContains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result = h;
                    return false; // found it; stop enumerating
                }
                return true;
            };
            EnumChildWindows(parent, cb, IntPtr.Zero);
            return result;
        }

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        /// <summary>Restores a window's original proc and drops its subclass. Call when the window
        /// closes so a recycled HWND doesn't keep a stale subclass.</summary>
        public static void RemoveWindowHook(IntPtr hWnd)
        {
            if (_hookedWindows.TryGetValue(hWnd, out var hook))
            {
                SetWindowLongPtr(hWnd, GWL_WNDPROC, hook.Orig); // harmless if the HWND is already gone
                _hookedWindows.Remove(hWnd);
            }
        }

        private static IntPtr WindowSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (!_hookedWindows.TryGetValue(hWnd, out var hook))
                return DefWindowProc(hWnd, msg, wParam, lParam); // defensive; should never happen

            if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero && (hook.MinW > 0 || hook.MinH > 0))
            {
                double scale = GetDpiForWindow(hWnd) / 96.0;
                if (scale <= 0) scale = 1.0;
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                if (hook.MinW > 0) mmi.minTrackX = (int)(hook.MinW * scale);
                if (hook.MinH > 0) mmi.minTrackY = (int)(hook.MinH * scale);
                Marshal.StructureToPtr(mmi, lParam, false);
                return IntPtr.Zero;
            }

            // Eat the Alt+key menu-mnemonic beep for opted-in (menu-less) windows.
            if (msg == WM_SYSCHAR && hook.SuppressAltBeep)
                return IntPtr.Zero;

            return CallWindowProc(hook.Orig, hWnd, msg, wParam, lParam);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static bool ForceWindowToFront(IntPtr hWnd)
        {
            // Only un-minimize. Do NOT SW_RESTORE unconditionally — that un-maximizes a
            // maximized target window (e.g. a maximized Firefox drops to a floating size).
            if (IsIconic(hWnd))
                ShowWindow(hWnd, SW_RESTORE);
            SetWindowPos(hWnd, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            SetWindowPos(hWnd, (IntPtr)HWND_NOTOPMOST, 0, 0, 0, 0, SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE);
            bool result = SetForegroundWindow(hWnd);
            SetActiveWindow(hWnd);
            return result;
        }
    }

    public sealed class CaretContext
    {
        public IntPtr Hwnd;
        public int X;
        public int Y;

        public static CaretContext? Capture(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !GetCaretPos(out POINT pt)) return null;
            ClientToScreen(hwnd, ref pt);
            return new CaretContext { Hwnd = hwnd, X = pt.X, Y = pt.Y };
        }

        public void Restore()
        {
            WindowHelper.SetForegroundWindow(Hwnd);
            SetCursorPos(X, Y);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // down
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // up
        }

        [DllImport("user32.dll")] private static extern bool GetCaretPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    }
}