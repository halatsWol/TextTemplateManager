using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Windows.UI.Core;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using TextTemplateManager.Common;
using TextTemplateManager.Data;
using TextTemplateManager.Helpers;
using TextTemplateManager.Models;
using TextTemplateManager.Services.Pasting;
using Windows.System;
using System.Diagnostics;

namespace TextTemplateManager
{
    public sealed partial class PasteWindow : Window
    {
        private string _multiKeyBuffer = "";
        private bool _isAltPressed = false;
        private bool _isProcessing = false;
        private IntPtr _inputSiteHwnd = IntPtr.Zero;   // WinUI content-input child, subclassed for the Alt-beep
        private IntPtr _hwnd = IntPtr.Zero;

        public PasteWindow()
        {
            this.InitializeComponent();
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            this.Activated += (s, e) =>
            {
                if (e.WindowActivationState != WindowActivationState.Deactivated)
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        WindowHelper.ForceWindowToFront(_hwnd);

                        // Subclass the input child so the Alt-key WM_SYSCHAR ding is swallowed
                        // there. Retries on later activations if not yet realized.
                        if (_inputSiteHwnd == IntPtr.Zero)
                            _inputSiteHwnd = WindowHelper.SuppressAltMenuBeepOnChild(_hwnd);

                        SearchBox.Focus(FocusState.Programmatic);
                    });
                }
                else if (!_hasExecuted)
                {
                    // Lost focus mid-entry (e.g. clicked another window): abandon the multi-key
                    // entry so returning doesn't resume with a stale buffer or a stuck ALT.
                    CancelMultiKeyEntry();
                }
            };

            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(780, 500));
            ConfigureWindow();
            InstallAltEscHook();

            this.DispatcherQueue.TryEnqueue(() => LoadInitialData());

            // handledEventsToo: see keys even after SearchBox handles them.
            this.RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
            this.RootGrid.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnKeyUp), true);
        }

        private void LoadInitialData()
        {
            ResetExpansion(DataNode.Instance.LocalItems, false);
            UpdateAllFilters("");
            UpdateBufferUI();
        }

        private void ConfigureWindow()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            appWindow.SetIcon("Assets/AppIcon.ico");
            if (appWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            {
                p.IsResizable = true;
                p.IsMaximizable = false;   // quick-paste window shouldn't maximize
            }

            WindowHelper.SetWindowMinSize(hWnd, 520, 400);   // small floor, below the main window
            this.Closed += (s, e) =>
            {
                WindowHelper.RemoveWindowHook(hWnd);
                if (_inputSiteHwnd != IntPtr.Zero) WindowHelper.RemoveWindowHook(_inputSiteHwnd);
                RemoveAltEscHook();
            };
        }

        #region Search & Filtering
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isProcessing) return;
            try
            {
                _isProcessing = true;
                UpdateAllFilters(SearchBox.Text);
            }
            finally { _isProcessing = false; }
        }

        private void UpdateAllFilters(string searchFilter)
        {
            TemplateTree.RootNodes.Clear();
            ClearPreview();

            if (string.IsNullOrWhiteSpace(searchFilter))
            {
                ResetExpansion(DataNode.Instance.LocalItems, false);
                foreach (var item in DataNode.Instance.LocalItems)
                    TemplateTree.RootNodes.Add(CreateNode(item));
            }
            else
            {
                var filteredResults = FilterTreeWithHierarchy(DataNode.Instance.LocalItems, searchFilter);
                foreach (var item in filteredResults)
                    TemplateTree.RootNodes.Add(CreateNode(item));
            }

            if (string.IsNullOrEmpty(_multiKeyBuffer))
            {
                RefreshMultiKeyList(searchFilter);
                RefreshSingleKeyList(searchFilter);
            }
        }

        private void RefreshMultiKeyList(string searchFilter)
        {
            var allShortcuts = DataNode.Instance.AllItems.Where(t => !string.IsNullOrEmpty(t.MultiKeyShortcut)).ToList();
            foreach (var t in allShortcuts) t.EffectiveMultiKey = EffectiveMulti(t);
            StampSource(allShortcuts);
            MultiKeyList.ItemsSource = string.IsNullOrWhiteSpace(searchFilter)
                ? allShortcuts
                : allShortcuts.Where(t => t.Title.Contains(searchFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void RefreshSingleKeyList(string searchFilter)
        {
            // Winners only (local first, then sync order); duplicates hidden.
            var all = DataNode.Instance.WinningSingleKeyTemplates().ToList();
            StampSource(all);
            SingleKeyList.ItemsSource = string.IsNullOrWhiteSpace(searchFilter)
                ? all
                : all.Where(t => t.Title.Contains(searchFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void UpdateMultiKeyFilter(string shortcutBuffer)
        {
            string cleanKey = shortcutBuffer.TrimEnd('_');
            var allShortcuts = DataNode.Instance.AllItems.Where(t => !string.IsNullOrEmpty(t.MultiKeyShortcut)).ToList();
            foreach (var t in allShortcuts) t.EffectiveMultiKey = EffectiveMulti(t);
            StampSource(allShortcuts);
            MultiKeyList.ItemsSource = string.IsNullOrEmpty(cleanKey)
                ? allShortcuts
                : allShortcuts.Where(t => t.EffectiveMultiKey.StartsWith(cleanKey, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Stamp the source label (sync-folder name or "local") for the lists.
        private static void StampSource(IEnumerable<Template> items)
        {
            foreach (var t in items)
            {
                var src = DataNode.Instance.GetSyncSourceForItem(t);
                t.IsLocalSource = src == null;
                t.SourceLabel = src?.Name ?? "local";
            }
        }
        #endregion

        #region TreeView Helpers
        private void ResetExpansion(IEnumerable<BaseItem> items, bool expand)
        {
            foreach (var item in items)
            {
                item.IsExpanded = expand;
                if (item.Children.Any()) ResetExpansion(item.Children, expand);
            }
        }

        private TreeViewNode CreateNode(BaseItem item)
        {
            var node = new TreeViewNode() { Content = item, IsExpanded = item.IsExpanded };
            if (item.Children != null)
                foreach (var child in item.Children) node.Children.Add(CreateNode(child));
            return node;
        }

        private List<BaseItem> FilterTreeWithHierarchy(IEnumerable<BaseItem> items, string query)
        {
            var matches = new List<BaseItem>();
            foreach (var item in items)
            {
                item.IsExpanded = false;
                bool selfMatches = item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                  (item is Template t && (t.TagsCsv?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

                var matchingChildren = FilterTreeWithHierarchy(item.Children, query);
                if (selfMatches || matchingChildren.Count > 0)
                {
                    if (item is Folder && matchingChildren.Count > 0) item.IsExpanded = true;
                    matches.Add(item);
                }
            }
            return matches;
        }
        #endregion

        #region Input & Paste Logic
        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // ALT via event args — more reliable than InputKeyboardSource here.
            bool altIsDown = e.KeyStatus.IsMenuKeyDown || e.Key == VirtualKey.Menu;

            if (e.Key == VirtualKey.Escape)
            {
                // Help tip open → Esc closes it first, not the window. Also swallow an Esc the
                // light-dismiss layer already used.
                if (HelpTip.IsOpen || e.Handled)
                {
                    HelpTip.IsOpen = false;
                    e.Handled = true;
                    return;
                }

                if (altIsDown || _isAltPressed) return;   // Alt+Esc handled by the low-level hook

                var escFocus = FocusManager.GetFocusedElement(this.Content.XamlRoot);
                bool searching = ReferenceEquals(escFocus, SearchBox) && !string.IsNullOrEmpty(SearchBox.Text);
                if (!searching)   // don't close mid-search
                {
                    e.Handled = true;
                    this.Close();
                }
                return;
            }

            if (e.Key == VirtualKey.Menu)
            {
                _isAltPressed = true;
                // Holding ALT shows the multi-key list; remember the tab to restore on release.
                _savedTab ??= ShortcutTabs.SelectedItem as SelectorBarItem;
                ShortcutTabs.SelectedItem = TabMulti;
                e.Handled = true;
                return;
            }

            // Multi-key (ALT + key) — must precede the TextBox passthrough below.
            if (altIsDown || _isAltPressed)
            {
                e.Handled = true;
                HandleMultiKeyInput(e.Key);
                return;
            }

            // Single-key, resolved by priority (local, then sync order).
            var match = DataNode.Instance.ResolveSingleKey(e.Key.ToString());
            if (match != null && string.IsNullOrEmpty(SearchBox.Text))   // don't hijack an active search
            {
                e.Handled = true;
                ExecutePaste(match, false);
                return;
            }

            // Otherwise let the search box type normally.
            var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (focused is TextBox)
            {
                return;
            }

            // Outside the search box: a printable non-shortcut key beeps; other keys focus search.
            if (TryGetCharKey(e.Key, out _))
            {
                e.Handled = true;
                PlayNoMatchBeep();
            }
            else
            {
                SearchBox.Focus(FocusState.Programmatic);
            }
        }

        private void HandleMultiKeyInput(VirtualKey key)
        {
            if (key == VirtualKey.Back)
            {
                if (_multiKeyBuffer.Length > 0)
                {
                    _multiKeyBuffer = _multiKeyBuffer.Remove(_multiKeyBuffer.Length - 1);
                    UpdateMultiKeyFilter(_multiKeyBuffer);
                    UpdateBufferUI();
                }
                return;
            }

            // '-' and '.' are shortcut/prefix separators; '_' (Shift+'-') is the trailing
            // paste-as-plaintext modifier, allowed only at the end.
            if (TryGetSpecialChar(key, out char special))
            {
                if (_multiKeyBuffer.EndsWith("_")) return; // nothing types after a plaintext modifier
                string cand = _multiKeyBuffer + special;

                if (special == '_')
                {
                    // Only the trailing plaintext modifier; it can't start an entry.
                    if (_multiKeyBuffer.Length == 0) { PlayNoMatchBeep(); return; }
                }
                else if (!IsMultiKeyPrefix(cand)) { PlayNoMatchBeep(); return; }

                _multiKeyBuffer = cand;
                UpdateMultiKeyFilter(_multiKeyBuffer);
                UpdateBufferUI();
                return;
            }

            if (TryGetCharKey(key, out char c))
            {
                if (_multiKeyBuffer.EndsWith("_")) return;   // nothing types after the plaintext modifier
                string candidate = _multiKeyBuffer + c;
                if (!IsMultiKeyPrefix(candidate))
                {
                    PlayNoMatchBeep();   // would no longer begin any shortcut -> reject + error
                    return;
                }
                _multiKeyBuffer = candidate;
                UpdateMultiKeyFilter(_multiKeyBuffer);
                UpdateBufferUI();
            }
        }

        private void OnKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Menu)
            {
                _isAltPressed = false;
                // Released ALT → restore whichever tab was active before ALT was pressed.
                if (_savedTab != null) { ShortcutTabs.SelectedItem = _savedTab; _savedTab = null; }
                if (!string.IsNullOrEmpty(_multiKeyBuffer))
                {
                    // Match the buffer as-is first (so a '_' separator resolves); if that fails and
                    // it ends with '_', treat the trailing '_' as the paste-as-plaintext modifier.
                    var match = MatchEffective(_multiKeyBuffer);
                    bool forcePlain = false;
                    if (match == null && _multiKeyBuffer.EndsWith("_"))
                    {
                        match = MatchEffective(_multiKeyBuffer.TrimEnd('_'));
                        forcePlain = match != null;
                    }

                    if (match != null) ExecutePaste(match, forcePlain);

                    _multiKeyBuffer = "";
                    UpdateBufferUI();
                    RefreshMultiKeyList(SearchBox.Text);
                }
            }
        }

        // True if `prefix` begins at least one EFFECTIVE multi-key shortcut (prefix-namespaced).
        private static bool IsMultiKeyPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return true;
            return DataNode.Instance.AllItems.Any(t =>
                !string.IsNullOrEmpty(t.MultiKeyShortcut) &&
                EffectiveMulti(t).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        // Maps a letter/digit VirtualKey to its lowercase character; false for any other key.
        private static bool TryGetCharKey(VirtualKey key, out char c)
        {
            c = '\0';
            string s = key.ToString().Replace("Number", "");
            if (s.Length == 1 && char.IsLetterOrDigit(s[0])) { c = char.ToLowerInvariant(s[0]); return true; }
            return false;
        }

        private const uint MB_OK = 0x00000000;   // "Default Beep" — the Explorer no-match ding
        [DllImport("user32.dll")] private static extern bool MessageBeep(uint uType);
        private static void PlayNoMatchBeep() => MessageBeep(MB_OK);

        private static bool IsShiftDown()
            => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down)
               == CoreVirtualKeyStates.Down;

        // The typed multikey shortcut (sync-prefixed, e.g. "and-msg").
        private static string EffectiveMulti(Template t) => DataNode.Instance.GetEffectiveMultiKey(t);

        // Maps the '-'/'_' and '.' keys to their character (Shift+'-' = '_').
        private static bool TryGetSpecialChar(VirtualKey key, out char c)
        {
            c = '\0';
            switch (key)
            {
                case (VirtualKey)189:      // OEM_MINUS
                case VirtualKey.Subtract:  // numpad '-'
                    c = IsShiftDown() ? '_' : '-';
                    return true;
                case (VirtualKey)190:      // OEM_PERIOD
                case VirtualKey.Decimal:   // numpad '.'
                    c = '.';
                    return true;
                default:
                    return false;
            }
        }

        // Resolves a typed key to a template: exact effective match, else a UNIQUE effective prefix.
        private static Template MatchEffective(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var all = DataNode.Instance.AllItems.Where(t => !string.IsNullOrEmpty(t.MultiKeyShortcut)).ToList();
            var exact = all.FirstOrDefault(t => EffectiveMulti(t).Equals(key, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var prefix = all.Where(t => EffectiveMulti(t).StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
            return prefix.Count == 1 ? prefix[0] : null;
        }

        private bool _hasExecuted = false;

        private void ExecutePaste(Template item, bool forcePlain)
            => ExecutePaste(item, forcePlain ? PasteMode.Plaintext : item.DefaultPasteMode);

        private void ExecutePaste(Template item, PasteMode mode)
        {
            if (_hasExecuted) return;   // guard double-trigger
            _hasExecuted = true;

            this.Close();   // return focus to the target app first
            _ = PasteService.HandlePaste(item.Content, mode);
        }

        // Clears the in-progress multi-key entry. On a full reset (window lost focus) the held-ALT
        // state is cleared and the tab restored too, since the ALT key-up will never arrive.
        private void CancelMultiKeyEntry(bool fullReset = true)
        {
            if (fullReset)
            {
                _isAltPressed = false;
                if (_savedTab != null) { ShortcutTabs.SelectedItem = _savedTab; _savedTab = null; }
            }
            if (string.IsNullOrEmpty(_multiKeyBuffer)) return;
            _multiKeyBuffer = "";
            UpdateBufferUI();
            RefreshMultiKeyList(SearchBox.Text);
        }
        #endregion

        #region Alt+Esc hook
        // Alt+Esc is an OS window-switch shortcut, so it never reaches WinUI's KeyDown. A low-level
        // keyboard hook lets us swallow it while Quick Paste is foreground and cancel the entry
        // instead of letting Windows send the window to the back (which left ALT / the buffer stuck).
        private IntPtr _keyboardHook = IntPtr.Zero;
        private LowLevelKeyboardProc? _keyboardProc;

        private void InstallAltEscHook()
        {
            if (_keyboardHook != IntPtr.Zero) return;
            _keyboardProc = KeyboardHookProc;   // keep the delegate alive against GC
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
        }

        private void RemoveAltEscHook()
        {
            if (_keyboardHook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            _keyboardProc = null;
        }

        private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool altDown = (data.flags & LLKHF_ALTDOWN) != 0 || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                if (data.vkCode == VK_ESCAPE && altDown && GetForegroundWindow() == _hwnd)
                {
                    // ALT is still physically held, so keep the ALT state and just drop the buffer.
                    DispatcherQueue.TryEnqueue(() => CancelMultiKeyEntry(fullReset: false));
                    return (IntPtr)1;   // swallow Alt+Esc
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        private const int VK_ESCAPE = 0x1B, VK_MENU = 0x12;
        private const uint LLKHF_ALTDOWN = 0x20;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
        #endregion

        #region UI Updates & Preview

        // The preview reuses the editor's rendering (same HTML + editor.css) in a read-only
        // WebView, so it's pixel-accurate (colors, highlights, tables) and consistent.
        private bool _previewReady;
        private string _pendingPreviewHtml = "<p></p>";

        private async void PreviewWebView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await PreviewWebView.EnsureCoreWebView2Async();
                var core = PreviewWebView.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.WebMessageReceived += Preview_WebMessageReceived;

                // Inline preview.html + editor.css and navigate to the string (no fetch, no cache).
                string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "editor");
                string html = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "preview.html"));
                string css = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "editor.css"));
                html = System.Text.RegularExpressions.Regex.Replace(
                    html, "<link[^>]*editor\\.css[^>]*>", _ => $"<style>{css}</style>");

                PreviewWebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Preview] init failed: {ex.Message}");
            }
        }

        private void Preview_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var d = System.Text.Json.JsonDocument.Parse(args.TryGetWebMessageAsString());
                if (d.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready")
                {
                    _previewReady = true;
                    ApplyPreviewTheme();
                    PushPreview(_pendingPreviewHtml);
                }
            }
            catch { }
        }

        private void ApplyPreviewTheme()
        {
            if (PreviewWebView?.CoreWebView2 == null) return;
            bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
            _ = PreviewWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.previewApi && window.previewApi.setTheme({(dark ? "true" : "false")})");
        }

        private async void PushPreview(string html)
        {
            if (!_previewReady || PreviewWebView?.CoreWebView2 == null) return;
            try
            {
                string arg = System.Text.Json.JsonSerializer.Serialize(html ?? string.Empty);
                await PreviewWebView.CoreWebView2.ExecuteScriptAsync($"window.previewApi.setContent({arg})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Preview] setContent failed: {ex.Message}");
            }
        }

        private void ClearPreview()
        {
            _pendingPreviewHtml = "<p></p>";
            PushPreview("<p></p>");
        }

        private void UpdatePreview(string content)
        {
            string html = content ?? string.Empty;
            // Legacy RTF templates are converted so they preview too.
            if (HtmlUtils.LooksLikeRtf(html))
            {
                try { html = RtfPipe.Rtf.ToHtml(html); } catch { html = string.Empty; }
            }
            _pendingPreviewHtml = html;
            PushPreview(html);
        }

        private void UpdateBufferUI()
        {
            Brush GetThemeBrush(string key, Windows.UI.Color fallback) =>
                (Application.Current.Resources.TryGetValue(key, out object val) && val is Brush b) ? b : new SolidColorBrush(fallback);

            if (string.IsNullOrEmpty(_multiKeyBuffer))
            {
                BufferDisplay.Text = "---";
                BufferDisplay.Opacity = 0.5;
                PlaintextBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                string displayPart = _multiKeyBuffer.TrimEnd('_').ToUpper();
                BufferDisplay.Text = displayPart;
                BufferDisplay.Opacity = 1.0;

                // Compare against the EFFECTIVE (sync-prefixed) shortcut, not the raw one, so a
                // valid prefix like "AND-MSG" isn't flagged red just because the raw shortcut is "MSG".
                bool exists = DataNode.Instance.AllItems.Any(t =>
                    !string.IsNullOrEmpty(t.MultiKeyShortcut) &&
                    EffectiveMulti(t).StartsWith(displayPart, StringComparison.OrdinalIgnoreCase));
                BufferDisplay.Foreground = exists ? GetThemeBrush("AccentTextFillColorPrimaryBrush", Colors.DeepSkyBlue) : new SolidColorBrush(Colors.Crimson);
                PlaintextBadge.Visibility = _multiKeyBuffer.EndsWith("_") ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void TemplateTree_SelectionChanged(TreeView sender, object args)
        {
            if (args is TreeViewSelectionChangedEventArgs selection && selection.AddedItems.Count > 0)
            {
                var item = selection.AddedItems[0];
                if (item is TreeViewNode node && node.Content is Template t) UpdatePreview(t.Content);
                else if (item is Template temp) UpdatePreview(temp.Content);
            }
            else ClearPreview();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e) => HelpTip.IsOpen = true;

        // ---- Tree double-click / right-click ----
        private Template? _contextTemplate;

        private void Tree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TreeViewNode node && node.Content is Template t)
                ExecutePaste(t, false);   // default paste — same as pressing the shortcut
        }

        private void Tree_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // Capture the right-clicked template so the context-menu handlers know the target.
            _contextTemplate = (sender is FrameworkElement fe && fe.DataContext is TreeViewNode node)
                ? node.Content as Template
                : null;
        }

        private void CtxPaste_Click(object sender, RoutedEventArgs e)
        {
            if (_contextTemplate != null) ExecutePaste(_contextTemplate, false);
        }

        private void CtxPasteAs_Click(object sender, RoutedEventArgs e)
        {
            if (_contextTemplate != null && sender is MenuFlyoutItem mi && mi.Tag is string tag
                && Enum.TryParse<PasteMode>(tag, out var mode))
                ExecutePaste(_contextTemplate, mode);
        }

        // ---- Single Key / Multi Key tabs ----
        private SelectorBarItem? _savedTab;   // tab active before ALT was pressed, restored on release

        private void ShortcutTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            bool multi = ReferenceEquals(sender.SelectedItem, TabMulti);
            SingleKeyList.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
            MultiKeyList.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShortcutItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Template t)
                ExecutePaste(t, false);   // default paste — same as pressing the shortcut
        }
        #endregion
    }
}