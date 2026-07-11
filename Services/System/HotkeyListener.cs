using System;
using System.Runtime.InteropServices;
using Windows.System;

namespace TextTemplateManager.Services.System;

public class HotkeyListener : IDisposable
{
    private IntPtr _hwnd;
    private readonly WndProc _wndProcDelegate;
    private const int HOTKEY_ID = 9000;

    // Win32 Constants
    private const int WM_HOTKEY = 0x0312;
    private const string WindowClassName = "TTM_HotkeyMessageWindow";

    public event Action HotkeyPressed;

    public HotkeyListener()
    {
        // We must keep the delegate alive so the GC doesn't collect it
        _wndProcDelegate = CustomWndProc;
        CreateMessageWindow();
    }

    private void CreateMessageWindow()
    {
        WNDCLASSEX vic = new WNDCLASSEX();
        vic.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
        vic.lpfnWndProc = _wndProcDelegate;
        vic.lpszClassName = WindowClassName;

        if (RegisterClassEx(ref vic) == 0)
        {
            // Handle error or if class already registered
        }

        // HWND_MESSAGE (-3) makes it a message-only window
        _hwnd = CreateWindowEx(0, WindowClassName, "TTM_Internal", 0, 0, 0, 0, 0, (IntPtr)(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && (int)wParam == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke();
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Register(string hotkeyStr)
    {
        UnregisterHotKey(_hwnd, HOTKEY_ID);

        uint modifiers = 0;
        uint vk = 0;

        var parts = hotkeyStr.Split('+');
        foreach (var part in parts)
        {
            switch (part.Trim())
            {
                case "Ctrl": modifiers |= 0x0002; break;
                case "Alt": modifiers |= 0x0001; break;
                case "Shift": modifiers |= 0x0004; break;
                case "Win": modifiers |= 0x0008; break;
                default:
                    if (Enum.TryParse<VirtualKey>(part, out var key))
                        vk = (uint)key;
                    break;
            }
        }

        if (vk != 0)
            RegisterHotKey(_hwnd, HOTKEY_ID, modifiers, vk);
    }

    #region Win32 Imports
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    #endregion

    public void Dispose()
    {
        UnregisterHotKey(_hwnd, HOTKEY_ID);
    }
}