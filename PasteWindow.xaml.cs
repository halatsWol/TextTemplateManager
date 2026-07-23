using H.NotifyIcon;   // Window.Show()/Hide() extension methods (same as the main window uses)
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using TextTemplateManager.Common;
using TextTemplateManager.Data;
using TextTemplateManager.Helpers;
using TextTemplateManager.Models;
using TextTemplateManager.Services.Pasting;
using Windows.System;
using Windows.UI.Core;

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

                        // Open in "shortcut mode" (focus the root, not search) so a single-key
                        // shortcut pastes immediately; typing a letter or clicking moves into search.
                        RootGrid.Focus(FocusState.Programmatic);
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

            this.DispatcherQueue.TryEnqueue(() => LoadInitialData());

            // handledEventsToo: see keys even after SearchBox handles them.
            this.RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
            this.RootGrid.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnKeyUp), true);
        }

        private void LoadInitialData()
        {
            UpdateAllFilters("");
            UpdateBufferUI();
        }

        /// <summary>Reveal the window for a hotkey press. Resets per-open state, arms the Alt+Esc hook,
        /// then shows + foregrounds + focuses shortcut mode. The instance is reused (hidden between
        /// uses), so the tree and WebView preview stay built and the window is instantly ready for a
        /// single-key press instead of leaking that key to the app underneath while it cold-loads.</summary>
        public void ShowForPaste()
        {
            ResetForShow();
            InstallAltEscHook();
            this.Show();
            WindowHelper.SetForegroundWindow(_hwnd);   // input recipient before Activate
            this.Activate();                           // Activated -> ForceWindowToFront + focus RootGrid
        }

        // Hide (not close) so the instance stays warm for the next open; drop the global hook while idle.
        private void Dismiss()
        {
            RemoveAltEscHook();
            this.Hide();
        }

        /// <summary>Warm the preview WebView ahead of first use (called shortly after app launch) so the
        /// first hotkey open isn't cold. Safe before the window is ever shown; if the WebView can't
        /// initialize until then, the Loaded handler picks it up and the setup still runs exactly once.</summary>
        public void Prewarm() => _ = EnsurePreviewAsync();

        // Clear whatever the previous session left so the window opens in the default shortcut mode.
        private void ResetForShow()
        {
            _hasExecuted = false;
            _isAltPressed = false;
            _multiKeyBuffer = "";
            _savedTab = null;

            _isProcessing = true;              // don't let clearing the box re-run the filter
            SearchBox.Text = "";
            _isProcessing = false;
            ShortcutTabs.SelectedItem = TabSingle;

            UpdateAllFilters("");              // unfiltered tree + cleared preview
            UpdateBufferUI();
            RootGrid.Focus(FocusState.Programmatic);   // shortcut mode: a single key pastes immediately
        }

        private void ConfigureWindow()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            appWindow.SetIcon("Assets/AppIcon.ico");

            // The X hides the window (keeping it warm) instead of destroying it, like the main window.
            // The app only really exits via the tray's Quit (Environment.Exit), which bypasses this.
            appWindow.Closing += (s, e) => { e.Cancel = true; Dismiss(); };

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
                foreach (var item in DataNode.Instance.LocalItems)
                    TemplateTree.RootNodes.Add(CreateNode(item));
            }
            else
            {
                foreach (var item in DataNode.Instance.LocalItems)
                {
                    var node = BuildFilteredNode(item, searchFilter);
                    if (node != null) TemplateTree.RootNodes.Add(node);
                }
            }

            if (string.IsNullOrEmpty(_multiKeyBuffer))
            {
                RefreshMultiKeyList(searchFilter);
                RefreshSingleKeyList(searchFilter);
            }
        }

        private void RefreshMultiKeyList(string searchFilter)
        {
            var allShortcuts = DataNode.Instance.AllItems
                .Where(t => !string.IsNullOrEmpty(t.MultiKeyShortcut))
                .OrderBy(DataNode.Instance.GetSourcePriority)   // local first, then sync order
                .ToList();
            foreach (var t in allShortcuts) t.EffectiveMultiKey = EffectiveMulti(t);
            StampSource(allShortcuts);
            MultiKeyList.ItemsSource = string.IsNullOrWhiteSpace(searchFilter)
                ? allShortcuts
                : allShortcuts.Where(t => t.Title.Contains(searchFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            MultiKeyList.SelectedIndex = -1;   // armed only while a buffer is typed under ALT
        }

        private void RefreshSingleKeyList(string searchFilter)
        {
            // All single-key templates, local first then sync order — duplicates across areas are shown
            // (a direct single-key press still resolves local-first via ResolveSingleKey).
            var all = DataNode.Instance.AllItems
                .Where(t => !string.IsNullOrWhiteSpace(t.SingleKeyShortcut))
                .OrderBy(DataNode.Instance.GetSourcePriority)
                .ToList();
            StampSource(all);
            SingleKeyList.ItemsSource = string.IsNullOrWhiteSpace(searchFilter)
                ? all
                : all.Where(t => t.Title.Contains(searchFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            SingleKeyList.SelectedIndex = SingleKeyList.Items.Count > 0 ? 0 : -1;
        }

        private void UpdateMultiKeyFilter(string shortcutBuffer)
        {
            string cleanKey = shortcutBuffer.TrimEnd('_');
            var allShortcuts = DataNode.Instance.AllItems
                .Where(t => !string.IsNullOrEmpty(t.MultiKeyShortcut))
                .OrderBy(DataNode.Instance.GetSourcePriority)   // local first, then sync order
                .ToList();
            foreach (var t in allShortcuts) t.EffectiveMultiKey = EffectiveMulti(t);
            StampSource(allShortcuts);
            MultiKeyList.ItemsSource = string.IsNullOrEmpty(cleanKey)
                ? allShortcuts
                : allShortcuts.Where(t => t.EffectiveMultiKey.StartsWith(cleanKey, StringComparison.OrdinalIgnoreCase)).ToList();
            // Highlight the top match (what Enter/release commits); reset on every keystroke. The
            // highlight stays even with an empty buffer so Enter can still paste it — release,
            // though, only fires when something is typed (see OnKeyUp).
            MultiKeyList.SelectedIndex = MultiKeyList.Items.Count > 0 ? 0 : -1;
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
        // Quick Paste owns its own node tree, so expansion lives on the nodes — never write
        // BaseItem.IsExpanded here: that model is shared with the main window and would collapse it.
        private TreeViewNode CreateNode(BaseItem item)
        {
            var node = new TreeViewNode() { Content = item, IsExpanded = false };
            if (item.Children != null)
                foreach (var child in item.Children) node.Children.Add(CreateNode(child));
            return node;
        }

        // A node with only the matching descendants (matched folders expanded); null if nothing matches.
        private TreeViewNode? BuildFilteredNode(BaseItem item, string query)
        {
            var childNodes = new List<TreeViewNode>();
            foreach (var child in item.Children)
            {
                var cn = BuildFilteredNode(child, query);
                if (cn != null) childNodes.Add(cn);
            }

            if (!ItemMatches(item, query) && childNodes.Count == 0) return null;

            var node = new TreeViewNode { Content = item, IsExpanded = childNodes.Count > 0 };
            foreach (var cn in childNodes) node.Children.Add(cn);
            return node;
        }

        private static bool ItemMatches(BaseItem item, string query)
            => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (item is Template t && (t.TagsCsv?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
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

                // Esc steps focus back toward shortcut mode before it ever closes the window:
                // tree → search box → shortcut mode → close (so from the tree the 3rd Esc closes).
                if (IsTreeFocused())
                {
                    e.Handled = true;
                    SearchBox.Focus(FocusState.Programmatic);
                    return;
                }

                var escFocus = FocusManager.GetFocusedElement(this.Content.XamlRoot);
                if (ReferenceEquals(escFocus, SearchBox))
                {
                    // Leave the search box (clearing any filter) so single/multi-key and list
                    // navigation work again; a further Esc, already in shortcut mode, closes.
                    e.Handled = true;
                    SearchBox.Text = "";
                    RootGrid.Focus(FocusState.Programmatic);
                    return;
                }

                e.Handled = true;
                Dismiss();   // shortcut mode already -> Esc hides (keep warm for next open)
                return;
            }

            if (e.Key == VirtualKey.Menu)
            {
                _isAltPressed = true;
                // Holding ALT shows the multi-key list; remember the tab to restore on release.
                _savedTab ??= ShortcutTabs.SelectedItem as SelectorBarItem;
                ShortcutTabs.SelectedItem = TabMulti;
                MultiKeyList.SelectedIndex = MultiKeyList.Items.Count > 0 ? 0 : -1;   // Enter can commit this
                // Pull focus off the tree so arrows/typing drive the multi-key entry, not the tree.
                if (IsTreeFocused()) RootGrid.Focus(FocusState.Programmatic);
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

            // While the tree has focus it owns navigation: the TreeView handles the arrows itself
            // (left/right collapse/expand) and Enter pastes the selected template. Focus elsewhere
            // (or Esc, above) hands navigation back to the shortcut lists.
            bool treeFocused = IsTreeFocused();
            if (treeFocused)
            {
                // Enter/Space paste a template, or expand/collapse a folder.
                if (e.Key is VirtualKey.Enter or VirtualKey.Space)
                {
                    e.Handled = true;
                    if (TemplateTree.SelectedItem is TreeViewNode node)
                    {
                        if (node.Content is Template t) ExecutePaste(t, false);
                        else node.IsExpanded = !node.IsExpanded;
                    }
                    return;
                }
                if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right)
                    return;
            }
            // Single-key tab: arrow up/down browse the list, Enter pastes the highlighted row.
            else if (IsSingleTabActive())
            {
                if (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down)
                {
                    var f = FocusManager.GetFocusedElement(this.Content.XamlRoot);
                    if (!ReferenceEquals(f, SingleKeyList))   // if the list is focused its own nav handles it
                    {
                        NavigateList(SingleKeyList, e.Key == VirtualKey.Down ? 1 : -1);
                        e.Handled = true;
                    }
                    return;
                }
                if (e.Key == VirtualKey.Enter)
                {
                    if (SingleKeyList.SelectedItem is Template sel) { e.Handled = true; ExecutePaste(sel, false); }
                    return;
                }
            }

            // Single-key shortcuts fire only in "shortcut mode" — never while the search box or the
            // tree has focus, where the letter belongs to the search/tree. (A SearchBox.Text check
            // isn't enough: the text is still empty on the FIRST keystroke into search, so that first
            // letter would be hijacked into a paste.)
            var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);
            bool searchFocused = focused is TextBox;

            if (!searchFocused && !treeFocused)
            {
                var match = DataNode.Instance.ResolveSingleKey(e.Key.ToString());
                if (match != null)
                {
                    e.Handled = true;
                    ExecutePaste(match, false);
                    return;
                }
            }

            if (searchFocused) return;   // in the search box: let it type normally

            // Shortcut mode, a key that isn't a shortcut: a printable one starts a search (focus the
            // box and insert it); other keys just move focus there. While the tree is navigating, keep
            // its focus and only beep on a stray letter.
            if (TryGetCharKey(e.Key, out char typed))
            {
                e.Handled = true;
                if (treeFocused)
                {
                    PlayNoMatchBeep();
                }
                else
                {
                    SearchBox.Focus(FocusState.Programmatic);
                    SearchBox.Text += typed;
                    SearchBox.SelectionStart = SearchBox.Text.Length;
                }
            }
            else if (!treeFocused)
            {
                SearchBox.Focus(FocusState.Programmatic);
            }
        }

        private void HandleMultiKeyInput(VirtualKey key)
        {
            // Arrow up/down move the highlight without changing the buffer (so typing can continue
            // where it left off). Enter commits the highlighted row even with an empty buffer.
            if (key == VirtualKey.Up || key == VirtualKey.Down)
            {
                NavigateList(MultiKeyList, key == VirtualKey.Down ? 1 : -1);
                return;
            }
            if (key == VirtualKey.Enter) { PasteHighlightedMulti(requireBuffer: false); return; }

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
            if (_hasExecuted) return;   // already pasted (e.g. via Enter) — window is closing
            if (e.Key == VirtualKey.Menu)
            {
                _isAltPressed = false;
                // Release is armed only if something is still typed: paste the highlighted row
                // (trailing '_' = plaintext). Typing then deleting everything leaves nothing to
                // paste; committing an untyped highlight is done with Enter instead.
                if (PasteHighlightedMulti(requireBuffer: true)) return;   // window closing

                // Nothing pasted → restore the tab active before ALT and clear the entry.
                if (_savedTab != null) { ShortcutTabs.SelectedItem = _savedTab; _savedTab = null; }
                _multiKeyBuffer = "";
                UpdateBufferUI();
                RefreshMultiKeyList(SearchBox.Text);
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

        private bool IsSingleTabActive() => ReferenceEquals(ShortcutTabs.SelectedItem, TabSingle);

        // True while focus is inside the template tree (the TreeViewItem is focused, not the TreeView).
        private bool IsTreeFocused()
        {
            var node = FocusManager.GetFocusedElement(this.Content.XamlRoot) as DependencyObject;
            while (node != null)
            {
                if (ReferenceEquals(node, TemplateTree)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }


        // Pastes the highlighted multi-key row (trailing '_' = plaintext). Returns true if a paste
        // fired. requireBuffer gates the passive ALT-release commit on having typed something;
        // Enter passes false so it commits the highlight even with an empty buffer.
        private bool PasteHighlightedMulti(bool requireBuffer)
        {
            if (requireBuffer && string.IsNullOrEmpty(_multiKeyBuffer)) return false;
            if (MultiKeyList.SelectedItem is not Template sel) return false;
            ExecutePaste(sel, _multiKeyBuffer.EndsWith("_"));
            return true;
        }

        private static void NavigateList(ListView list, int delta)
        {
            int count = list.Items.Count;
            if (count == 0) return;
            int cur = list.SelectedIndex;
            int next = cur < 0 ? (delta > 0 ? 0 : count - 1) : Math.Clamp(cur + delta, 0, count - 1);
            list.SelectedIndex = next;
            if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
        }

        private bool _hasExecuted = false;

        private void ExecutePaste(Template item, bool forcePlain)
            => ExecutePaste(item, forcePlain ? PasteMode.Plaintext : item.DefaultPasteMode);

        private void ExecutePaste(Template item, PasteMode mode)
        {
            if (_hasExecuted) return;   // guard double-trigger
            _hasExecuted = true;

            Dismiss();   // hide (keep warm); HandlePaste re-targets the captured app itself
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

        private bool _previewConfigured;

        private async void PreviewWebView_Loaded(object sender, RoutedEventArgs e) => await EnsurePreviewAsync();

        // Idempotent: driven by the Loaded event and by Prewarm (which may run before the window is ever
        // shown). EnsureCoreWebView2Async is safe to call repeatedly; the one-time setup + navigation
        // guards on _previewConfigured so it runs exactly once whichever call reaches it first.
        private async System.Threading.Tasks.Task EnsurePreviewAsync()
        {
            try
            {
                await PreviewWebView.EnsureCoreWebView2Async();
                if (_previewConfigured) return;
                _previewConfigured = true;

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