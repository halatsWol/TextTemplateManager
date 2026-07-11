using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace TextTemplateManager.Helpers
{
    internal class ClipboardHelper
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        /// <summary>
        /// Checks if the clipboard is currently locked by another process.
        /// </summary>
        public static bool IsClipboardLocked()
        {
            // Try to open it. If we can't, it's locked.
            if (OpenClipboard(IntPtr.Zero))
            {
                CloseClipboard();
                return false; // We were able to open it, so it's not locked.
            }
            return true; // Failed to open, likely locked.
        }

        /// <summary>
        /// Waits until the clipboard is free or times out.
        /// </summary>
        public static async Task<bool> WaitForClipboardAsync(int timeoutMs = 1000)
        {
            int elapsed = 0;
            while (IsClipboardLocked() && elapsed < timeoutMs)
            {
                await Task.Delay(50);
                elapsed += 50;
            }
            return !IsClipboardLocked();
        }
    }
}