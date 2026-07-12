using System;
using System.Text;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MarflowSoftware.Helpers;
using TextTemplateManager.Common;
using TextTemplateManager.Helpers;
using TextTemplateManager.Services.Pasting.Strategies;
using Windows.ApplicationModel.DataTransfer;

namespace TextTemplateManager.Services.Pasting
{
    /// <summary>
    /// Orchestrates the clipboard swap, paste simulation, and original data restoration.
    /// Templates are stored as HTML; paste modes are Plaintext, Markdown, RTF, HTML, Jira and Auto.
    /// </summary>
    public static class PasteService
    {
        private static string SafeHtmlToRtf(string html)
        {
            try { return HtmlConverter.ConvertHtmlToRtf(html); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PASTE SERVICE] HTML->RTF failed: {ex.Message}");
                return string.Empty;
            }
        }

        // ---- Win32 clipboard helpers ----

        private const uint CF_UNICODETEXT = 13;
        private static readonly uint CfHtml = RegisterClipboardFormat("HTML Format");
        private static readonly uint CfRtf = RegisterClipboardFormat("Rich Text Format");

        private static byte[] Utf8Bytes(string s) => Encoding.UTF8.GetBytes((s ?? string.Empty) + "\0");
        private static byte[] UnicodeBytes(string s) => Encoding.Unicode.GetBytes((s ?? string.Empty) + "\0");
        private static byte[] RtfBytes(string s) => Encoding.GetEncoding(1252).GetBytes((s ?? string.Empty) + "\0");

        /// <summary>Wraps an HTML fragment in a valid CF_HTML payload with correct byte offsets.</summary>
        private static string BuildCfHtml(string fragment)
        {
            const string header =
                "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            const string pre = "<html>\r\n<body>\r\n<!--StartFragment-->";
            const string post = "<!--EndFragment-->\r\n</body>\r\n</html>";

            var enc = Encoding.UTF8;
            int headerLen = enc.GetByteCount(string.Format(header, 0, 0, 0, 0));
            int startHtml = headerLen;
            int startFragment = startHtml + enc.GetByteCount(pre);
            int endFragment = startFragment + enc.GetByteCount(fragment);
            int endHtml = endFragment + enc.GetByteCount(post);
            return string.Format(header, startHtml, endHtml, startFragment, endFragment) + pre + fragment + post;
        }

        /// <summary>
        /// Writes the given (clipboard-format-id → bytes) map to the OS clipboard via Win32.
        /// Eager and reliable — no delayed rendering, no Flush. Retries OpenClipboard because
        /// WebView2/Chromium's clipboard monitor may briefly hold it right after we set content.
        /// </summary>
        private static async Task<bool> SetClipboardFormatsAsync(Dictionary<uint, byte[]> formats)
        {
            const uint GMEM_MOVEABLE = 0x0002;

            bool opened = false;
            for (int i = 0; i < 15; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) { opened = true; break; }
                await Task.Delay(40);
            }
            if (!opened) { Debug.WriteLine("[PASTE] OpenClipboard failed after retries"); return false; }

            try
            {
                EmptyClipboard();
                foreach (var kv in formats)
                {
                    byte[] data = kv.Value;
                    IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
                    if (hMem == IntPtr.Zero) continue;
                    IntPtr ptr = GlobalLock(hMem);
                    if (ptr == IntPtr.Zero) { GlobalFree(hMem); continue; }
                    Marshal.Copy(data, 0, ptr, data.Length);
                    GlobalUnlock(hMem);
                    if (SetClipboardData(kv.Key, hMem) == IntPtr.Zero)
                        GlobalFree(hMem); // system did not take ownership
                }
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>Prepares a template's stored content the way a paste does: legacy RTF is
        /// migrated to HTML, template variables are substituted, colours normalized to hex.</summary>
        private static (string html, string plainText) PrepareContent(string rawContent)
        {
            string sanitized = new string((rawContent ?? string.Empty).Where(ch => ch != (char)0).ToArray()).Trim();
            string html = sanitized;
            if (HtmlUtils.LooksLikeRtf(html))
            {
                try { html = RtfPipe.Rtf.ToHtml(html); }
                catch { html = string.Empty; }
            }
            html = VariableHelper.ProcessVariables(html);
            html = HtmlUtils.NormalizeColorsToHex(html);
            return (html, HtmlUtils.ToPlainText(html));
        }

        /// <summary>Builds the (clipboard-format-id → bytes) payloads for a given paste mode.</summary>
        private static Dictionary<uint, byte[]> BuildFormatsForMode(string html, string plainText, PasteMode mode)
        {
            var formats = new Dictionary<uint, byte[]>();
            switch (mode)
            {
                case PasteMode.Plaintext:
                    formats[CF_UNICODETEXT] = UnicodeBytes(plainText);
                    break;

                case PasteMode.Markdown:
                    formats[CF_UNICODETEXT] = UnicodeBytes(HtmlToMarkdown.Convert(html));
                    break;

                case PasteMode.RTF:
                    string rtf = SafeHtmlToRtf(html);
                    if (!string.IsNullOrEmpty(rtf)) formats[CfRtf] = RtfBytes(rtf);
                    formats[CF_UNICODETEXT] = UnicodeBytes(plainText);
                    break;

                case PasteMode.HTML:
                    // Styled tables (not data-panel-type) — broadest rendering in email/web clients.
                    string htmlOut = PanelHtml.ToStyledHtml(html);
                    if (!string.IsNullOrEmpty(htmlOut)) formats[CfHtml] = Utf8Bytes(BuildCfHtml(htmlOut));
                    formats[CF_UNICODETEXT] = UnicodeBytes(plainText);
                    break;

                case PasteMode.Jira:
                    // HTML only (no RTF) so Jira's editor takes the HTML clipboard path and parses
                    // our data-panel-type divs back into native panels. Plain text is the fallback
                    // for targets that ignore HTML.
                    string jira = PanelHtml.Prepare(html);
                    if (!string.IsNullOrEmpty(jira)) formats[CfHtml] = Utf8Bytes(BuildCfHtml(jira));
                    formats[CF_UNICODETEXT] = UnicodeBytes(plainText);
                    break;

                case PasteMode.Auto:
                default:
                    // Most-compatible payload: panels as styled boxes (render in every target,
                    // including Jira as a table) plus RTF and plain text. For native Jira panels
                    // use the HTML/Jira mode.
                    string autoHtml = PanelHtml.ToStyledHtml(html);
                    if (!string.IsNullOrEmpty(autoHtml)) formats[CfHtml] = Utf8Bytes(BuildCfHtml(autoHtml));
                    string autoRtf = SafeHtmlToRtf(html);
                    if (!string.IsNullOrEmpty(autoRtf)) formats[CfRtf] = RtfBytes(autoRtf);
                    formats[CF_UNICODETEXT] = UnicodeBytes(plainText);
                    break;
            }
            return formats;
        }

        /// <summary>
        /// Formats a template's content for the given paste mode and places it on the clipboard
        /// WITHOUT pasting — used by the tree's "Copy" / "Copy As" actions. The content stays on
        /// the clipboard for the user to paste manually.
        /// </summary>
        public static async Task CopyToClipboardAsync(string content, PasteMode mode)
        {
            var (html, plainText) = PrepareContent(content);
            var formats = BuildFormatsForMode(html, plainText, mode);
            await SetClipboardFormatsAsync(formats);
        }

        public static async Task HandlePaste(string rtfContent, PasteMode mode)
        {
            IntPtr targetHwnd = WindowHelper.GetTargetWindow();

            // 1. Snapshot whatever the user currently has on the clipboard, so we can put it
            // back after the paste completes (see restore at the end of this method).
            var previousClipboard = await SnapshotClipboardAsync();

            // 2. Prepare Content
            string sanitizedRtf = rtfContent?.Replace("\u0000", "").Trim() ?? "";
            // Templates are now stored as HTML (WebView editor). Convert any legacy RTF so old
            // templates still paste correctly until they migrate to HTML on next edit.
            string html = sanitizedRtf;
            if (HtmlUtils.LooksLikeRtf(html))
            {
                try { html = RtfPipe.Rtf.ToHtml(html); }
                catch { html = string.Empty; }
            }

            // Substitute template variables ($[DATE], $[TIME], $[YEAR]) in the HTML, then derive
            // plain text from it so both representations stay consistent.
            html = VariableHelper.ProcessVariables(html);
            // Normalize rgb()/rgba() colours to #rrggbb — hex is the most portable form across
            // paste targets (Word, Outlook, browsers) and some editors' paste sanitizers only
            // recognize hex colours.
            html = HtmlUtils.NormalizeColorsToHex(html);
            string plainText = HtmlUtils.ToPlainText(html);

            // 3. Build the clipboard payloads for this mode and write them to the OS clipboard
            // via the Win32 API. This is EAGER (no delayed rendering), builds a correct CF_HTML
            // header ourselves, and avoids WinRT's Flush lock and stray "Preferred DropEffect".
            var formats = BuildFormatsForMode(html, plainText, mode);

            await SetClipboardFormatsAsync(formats);

            // 5. EXECUTE PASTE
            // Bring the original target back to the foreground first.
            WindowHelper.ForceWindowToFront(targetHwnd);

            // When the paste is triggered by releasing ALT (multi-key shortcut), the ALT
            // key may still be physically/logically down at this instant. Synthesizing
            // Ctrl+V while ALT is held makes the target receive Alt+Ctrl+V (or enter menu
            // mode), so nothing pastes. Wait for all modifiers to actually release before
            // sending the keystroke. Single-key shortcuts hold no modifier, so this returns
            // almost immediately.
            await WaitForModifiersReleasedAsync();
            WindowHelper.ResetModifiers();

            await Task.Delay(20);
            SimulatePaste();

            // 6. Restore the user's original clipboard in the background. Fire-and-forget so
            // it never blocks the UI, and it waits first (see RestoreClipboardAsync) so the
            // target app has finished reading our pasted content before we swap it back — the
            // earlier plain-text bug came from restoring too eagerly. If the snapshot is empty
            // (clipboard was empty, or we failed to read it) we leave our content in place
            // rather than risk wiping the user's data.
            _ = RestoreClipboardAsync(previousClipboard);
        }

        /// <summary>Clipboard formats backed by GDI handles / special data that cannot be
        /// safely copied as a raw global-memory block; skipped when snapshotting.</summary>
        private static readonly HashSet<uint> UnsafeSnapshotFormats = new()
        {
            2,     // CF_BITMAP
            3,     // CF_METAFILEPICT
            9,     // CF_PALETTE
            14,    // CF_ENHMETAFILE
            0x80,  // CF_OWNERDISPLAY
            0x82,  // CF_DSPBITMAP
            0x83,  // CF_DSPMETAFILEPICT
            0x8E,  // CF_DSPENHMETAFILE
        };

        /// <summary>
        /// Copies the current clipboard's global-memory formats (text/HTML/RTF/registered
        /// formats) into an in-memory map. Best-effort: GDI-object formats are skipped, and
        /// any handle we cannot lock is ignored. Returns an empty map if the clipboard could
        /// not be opened.
        /// </summary>
        private static async Task<Dictionary<uint, byte[]>> SnapshotClipboardAsync()
        {
            var snapshot = new Dictionary<uint, byte[]>();

            bool opened = false;
            for (int i = 0; i < 10; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) { opened = true; break; }
                await Task.Delay(30);
            }
            if (!opened) return snapshot;

            try
            {
                uint fmt = 0;
                while ((fmt = EnumClipboardFormats(fmt)) != 0)
                {
                    if (UnsafeSnapshotFormats.Contains(fmt)) continue;
                    IntPtr h = GetClipboardData(fmt);
                    if (h == IntPtr.Zero) continue;
                    UIntPtr size = GlobalSize(h);
                    if (size == UIntPtr.Zero) continue;
                    IntPtr ptr = GlobalLock(h);
                    if (ptr == IntPtr.Zero) continue;
                    try
                    {
                        byte[] buf = new byte[(int)size];
                        Marshal.Copy(ptr, buf, 0, buf.Length);
                        snapshot[fmt] = buf;
                    }
                    finally { GlobalUnlock(h); }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[PASTE SERVICE] Clipboard snapshot failed: {ex.Message}"); }
            finally { CloseClipboard(); }

            return snapshot;
        }

        /// <summary>
        /// Waits for the target app to finish reading our pasted content, then puts the user's
        /// original clipboard contents back. No-op when the snapshot is empty.
        /// </summary>
        private static async Task RestoreClipboardAsync(Dictionary<uint, byte[]> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0) return;
            await Task.Delay(600);
            await SetClipboardFormatsAsync(snapshot);
        }

        /// <summary>
        /// Waits until the ALT/CTRL/SHIFT/WIN keys are physically released, or until the
        /// timeout elapses. Needed because a multi-key shortcut fires on ALT release, and
        /// the key may still be down when we synthesize Ctrl+V.
        /// </summary>
        private static async Task WaitForModifiersReleasedAsync(int timeoutMs = 600)
        {
            // 0x12 = Alt, 0x11 = Ctrl, 0x10 = Shift, 0x5B/0x5C = Win keys
            int[] modifiers = { 0x12, 0x11, 0x10, 0x5B, 0x5C };
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool anyDown = modifiers.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);
                if (!anyDown)
                {
                    // Small settle so the target window has focus before the keystroke.
                    await Task.Delay(30);
                    return;
                }
                await Task.Delay(15);
            }

            Debug.WriteLine("[PASTE SERVICE] Modifier release wait timed out; pasting anyway.");
        }

        private static void SimulatePaste()
        {
            // Release stuck modifiers
            byte[] modifiers = { 0x10, 0x11, 0x12, 0x5B, 0x5C };
            foreach (var key in modifiers)
                keybd_event(key, 0, 0x0002, UIntPtr.Zero);

            Thread.Sleep(20);

            // Ctrl + V
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, 0x0002, UIntPtr.Zero);
            keybd_event(0x11, 0, 0x0002, UIntPtr.Zero);
        }

        #region Win32 API

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll")]
        private static extern uint EnumClipboardFormats(uint format);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern UIntPtr GlobalSize(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        #endregion
    }
}