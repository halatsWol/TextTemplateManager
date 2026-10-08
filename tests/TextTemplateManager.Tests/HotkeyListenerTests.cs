using System.Runtime.InteropServices;
using TextTemplateManager.Services.System;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>A global hotkey another app already owns can't be registered; the listener has to say so,
/// otherwise Quick Paste silently never opens.</summary>
public class HotkeyListenerTests
{
    // One listener for the whole run: its window class keeps the first instance's window procedure.
    private static readonly HotkeyListener Listener = new();

    private const string Combo = "Ctrl+Shift+Alt+F9";
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, VK_F9 = 0x78;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [Fact]
    public void A_hotkey_owned_elsewhere_is_reported_and_free_ones_register()
    {
        Assert.True(Listener.Register(Combo));
        Assert.True(Listener.Register("None"));   // releases it again

        // Another owner (this thread, standing in for another app) takes the combination first.
        Assert.True(RegisterHotKey(IntPtr.Zero, 0xBEEF, MOD_CONTROL | MOD_SHIFT | MOD_ALT, VK_F9));
        try
        {
            Assert.False(Listener.Register(Combo));
        }
        finally { UnregisterHotKey(IntPtr.Zero, 0xBEEF); }

        Assert.True(Listener.Register(Combo));
        Assert.True(Listener.Register(""));
    }
}
