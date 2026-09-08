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
        private bool _closingForPaste = false;   // a paste is destroying the window (let the real close through)
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
                        // A paste is in flight (a double-click closes the window, then HandlePaste
                        // brings the TARGET app to the front): don't yank Quick Paste back to the
                        // front here, or the Ctrl+V lands in this window's search box and it gets
                        // stuck. On a real open _hasExecuted is false (reset in ShowForPaste).
                        if (_hasExecuted) return;

                        WindowHelper.ForceWindowToFront(_hwnd);

                        // Subclass the input child so the Alt-key WM_SYSCHAR ding is swallowed
                        // there. Retries on later activations if not yet realized.
                        if (_inputSiteHwnd == IntPtr.Zero)
                            _inputSiteHwnd = WindowHelper.SuppressAltMenuBeepOnChild(_hwnd);

                        // Open in "shortcut mode" (focus the root, not search) so a single-key
                        // shortcut pastes immediately; a letter/digit that isn't a shortcut beeps, and
                        // Tab or a click moves into search.
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

            // Focus landing anywhere in the window drives the shortcut-list highlight (see
            // SyncShortcutHighlight): visible in shortcut mode, cleared while search or the tree has focus.
            // GotFocus bubbles, so subscribing on RootGrid catches focus reaching any descendant.
            this.RootGrid.GotFocus += OnContentGotFocus;
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
            // A paste sets _closingForPaste so the real close goes through (App then rebuilds a fresh
            // warm one). The app only really exits via the tray's Quit (Environment.Exit), which bypasses this.
            appWindow.Closing += (s, e) =>
            {
                if (_closingForPaste) return;
                e.Cancel = true;
                Dismiss();
            };

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
            // Highlight the first row only in shortcut mode — while search/tree has focus the list stays
            // unselected (SyncShortcutHighlight re-selects it when focus returns).
            SingleKeyList.SelectedIndex = (InShortcutMode() && SingleKeyList.Items.Count > 0) ? 0 : -1;
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
                    // Back to the search box with the filter intact and the caret at the end, so one
                    // more Down steps straight back into the tree (a clean browse/return round trip).
                    e.Handled = true;
                    SearchBox.Focus(FocusState.Programmatic);
                    SearchBox.SelectionLength = 0;
                    SearchBox.SelectionStart = SearchBox.Text.Length;
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

            // Ctrl+F focuses the search box from anywhere (shortcut mode, tree, or search), selecting any
            // existing text to type over. Without this a bare 'F' in shortcut mode would just beep as a
            // non-shortcut key.
            if (e.Key == VirtualKey.F && IsCtrlDown())
            {
                e.Handled = true;
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
                return;
            }

            // Three explicit focus states drive the rest: the TREE (owns its own arrows/Enter), the
            // SEARCH box (arrows move the caret and can step down into the tree; letters filter), and
            // SHORTCUT mode (RootGrid focused — single keys paste and arrows browse the visible list).
            // Keeping them separate stops a key inheriting a neighbour's behaviour, the way the search
            // box used to pick up the shortcut-list arrows by accident.
            var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);
            bool searchFocused = focused is TextBox;
            bool treeFocused = IsTreeFocused();

            // Tab / Shift+Tab cycle deterministically between the three surfaces, handled here so the
            // framework's own tab navigation (which kept pulling focus onto a display-only shortcut-list
            // row) never runs. Forward order is shortcut mode -> search -> tree -> shortcut mode; Shift
            // reverses it. An empty tree is skipped so focus never lands on nothing.
            if (e.Key == VirtualKey.Tab)
            {
                e.Handled = true;
                bool back = IsShiftDown();
                if (searchFocused)
                {
                    if (back) FocusShortcutMode();
                    else if (!TryFocusTree()) FocusShortcutMode();
                }
                else if (treeFocused)
                {
                    if (back) FocusSearch(); else FocusShortcutMode();
                }
                else   // shortcut mode
                {
                    if (back) { if (!TryFocusTree()) FocusSearch(); }
                    else FocusSearch();
                }
                return;
            }

            // ---- Tree ----
            if (treeFocused)
            {
                // Enter/Space paste the selected template, or expand/collapse a folder.
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
                // The TreeView handles the arrows itself (up/down move, left/right collapse/expand).
                if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right)
                    return;
                // A printable key hands the tree back to the search box and keeps typing there, rather
                // than dead-ending with a beep. (Space is taken above; '-'/'.' aren't letters/digits,
                // so they fall through and are ignored, as before.)
                if (TryGetCharKey(e.Key, out char intoSearch))
                {
                    e.Handled = true;
                    FocusSearchWith(intoSearch);
                }
                return;
            }

            // ---- Search box ----
            if (searchFocused)
            {
                // Up: caret (collapsing any selection) to the very start — nothing sits above the box,
                // so it never leaves. Down: caret to the end; a second Down from the end steps into the
                // tree (when it has rows — otherwise it stays put).
                if (e.Key == VirtualKey.Up)
                {
                    e.Handled = true;
                    SearchBox.SelectionLength = 0;
                    SearchBox.SelectionStart = 0;
                    return;
                }
                if (e.Key == VirtualKey.Down)
                {
                    e.Handled = true;
                    if (DecideSearchDown(SearchBox.SelectionStart, SearchBox.SelectionLength,
                                         SearchBox.Text.Length, TemplateTree.RootNodes.Count) == SearchDown.EnterTree)
                        TryFocusTree();
                    else { SearchBox.SelectionLength = 0; SearchBox.SelectionStart = SearchBox.Text.Length; }
                    return;
                }
                // Enter here is deliberately inert for now (the shortcut list isn't the focus).
                // Swallowed so a later binding has a clean home. Alt+Enter never reaches this branch —
                // the ALT block above commits the highlighted multi-key row first.
                if (e.Key == VirtualKey.Enter)
                {
                    e.Handled = true;
                    return;
                }
                return;   // every other key types into the box normally
            }

            // ---- Shortcut mode (RootGrid focused) ----
            // Arrow up/down browse whichever shortcut tab is showing; Enter pastes the highlighted row.
            // Both tabs behave the same — the multi-key list is reachable this way as well as via ALT.
            var list = IsSingleTabActive() ? SingleKeyList : MultiKeyList;
            if (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down)
            {
                if (!ReferenceEquals(focused, list))   // if the list itself is focused, its own nav handles it
                {
                    NavigateList(list, e.Key == VirtualKey.Down ? 1 : -1);
                    e.Handled = true;
                }
                return;
            }
            if (e.Key == VirtualKey.Enter)
            {
                if (list.SelectedItem is Template sel) { e.Handled = true; ExecutePaste(sel, false); }
                return;
            }

            // A single-key shortcut pastes immediately in shortcut mode. (A SearchBox.Text check isn't
            // enough to gate this — the text is still empty on the FIRST keystroke into search, so that
            // first letter would be hijacked into a paste; the focus split above is what gates it.)
            var match = DataNode.Instance.ResolveSingleKey(e.Key.ToString());
            if (match != null)
            {
                e.Handled = true;
                ExecutePaste(match, false);
                return;
            }

            // Not a matching shortcut: in shortcut mode the keyboard is for shortcuts, so a stray letter
            // or digit just beeps rather than dropping into search. That keeps behaviour steady whether
            // or not a key happens to be a shortcut today — a key you're used to doing nothing can't
            // start pasting later just because a shortcut got assigned to it.
            if (TryGetCharKey(e.Key, out _))
            {
                e.Handled = true;
                PlayNoMatchBeep();
            }
            // Everything else — Tab, Shift, Caps Lock, other modifiers, '-'/'.'/Space — is left unhandled
            // so nothing drags focus out of shortcut mode: Tab lets the framework move to the search box in
            // one clean step (focusing it here as well would skip search and land on the tree), and a bare
            // modifier does nothing. Search is reached by Tab or a click.
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

        private static bool IsCtrlDown()
            => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down)
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

        // Shortcut mode = the keyboard is on the shortcut lists: focus is neither the search box nor the
        // tree. Single keys paste and the arrows browse the visible list in this state. Before the window
        // is shown (no XamlRoot yet) it opens straight into shortcut mode, so default to true.
        private bool InShortcutMode()
        {
            var root = this.Content?.XamlRoot;
            if (root is null) return true;
            return FocusManager.GetFocusedElement(root) is not TextBox && !IsTreeFocused();
        }

        // Focus moved: the visible shortcut list carries a highlighted row only while shortcut mode is
        // active, so it never looks live while you type in search or browse the tree. Leaving clears both
        // lists; coming back (Esc, Tab, or a click that didn't land on a row) re-selects the first row.
        private void OnContentGotFocus(object sender, RoutedEventArgs e) => SyncShortcutHighlight();

        private void SyncShortcutHighlight()
        {
            if (InShortcutMode())
            {
                var list = IsSingleTabActive() ? SingleKeyList : MultiKeyList;
                if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
                if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
            }
            else
            {
                SingleKeyList.SelectedIndex = -1;
                MultiKeyList.SelectedIndex = -1;
            }
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
            int next = NextIndex(list.SelectedIndex, delta, list.Items.Count);
            if (next < 0) return;
            list.SelectedIndex = next;
            if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
        }

        /// <summary>Where an arrow-key step lands, wrapping at both ends: past the last item goes back to
        /// the first, before the first goes to the last. With nothing selected yet, Down starts at the top
        /// and Up at the bottom. Returns -1 when there is nothing to select.</summary>
        internal static int NextIndex(int current, int delta, int count)
        {
            if (count <= 0) return -1;
            if (current < 0) return delta > 0 ? 0 : count - 1;
            return ((current + delta) % count + count) % count;   // stays in range for a negative delta
        }

        internal enum SearchDown { MoveCaretToEnd, EnterTree }

        /// <summary>What Down does from the search box: step into the tree, or just move the caret to the
        /// end. It enters the tree only from a collapsed caret already at the end (an empty box counts as
        /// at-the-end) and only when the tree has rows; a selection or a mid-string caret collapses to the
        /// end first, and an empty tree keeps the caret in the box.</summary>
        internal static SearchDown DecideSearchDown(int caret, int selectionLength, int textLength, int treeNodeCount)
        {
            bool atEnd = selectionLength == 0 && caret >= textLength;
            return atEnd && treeNodeCount > 0 ? SearchDown.EnterTree : SearchDown.MoveCaretToEnd;
        }

        // Move focus into the search box and append a character, caret at the end — the shared path for a
        // printable key pressed in shortcut mode or while the tree has focus.
        private void FocusSearchWith(char typed)
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.Text += typed;
            SearchBox.SelectionStart = SearchBox.Text.Length;
        }

        // Move focus into the tree, seeding a selection so Enter has a target and the preview fills (the
        // tree drives Enter off its SELECTED node, not the focused one, and nothing else seeds one).
        // Returns false when the tree is empty, so callers can route focus elsewhere.
        private bool TryFocusTree()
        {
            if (TemplateTree.RootNodes.Count == 0) return false;
            TemplateTree.SelectedNode ??= TemplateTree.RootNodes[0];
            if (TemplateTree.ContainerFromNode(TemplateTree.SelectedNode) is TreeViewItem container)
                container.Focus(FocusState.Programmatic);
            else
                TemplateTree.Focus(FocusState.Programmatic);
            return true;
        }

        private void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

        // Return to shortcut mode: focus the root grid (single keys paste, the arrows drive the list).
        private void FocusShortcutMode() => RootGrid.Focus(FocusState.Programmatic);

        private bool _hasExecuted = false;

        private void ExecutePaste(Template item, bool forcePlain)
            => ExecutePaste(item, forcePlain ? PasteMode.Plaintext : item.DefaultPasteMode);

        private void ExecutePaste(Template item, PasteMode mode)
        {
            if (_hasExecuted) return;   // guard double-trigger
            _hasExecuted = true;

            string content = item.Content;   // capture before the window is torn down

            // Defer out of the current input event so a mouse double-click finishes settling, then
            // CLOSE (destroy) the window rather than hide it: a merely-hidden window can still be
            // reactivated and swallow the Ctrl+V into its own search box — a closed one cannot. App
            // rebuilds a fresh, prewarmed instance on Closed for the next hotkey.
            DispatcherQueue.TryEnqueue(() =>
            {
                RemoveAltEscHook();
                _closingForPaste = true;
                this.Close();
                _ = PasteService.HandlePaste(content, mode);
            });
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

            // Highlight the newly-visible list's first row so the arrows and Enter have somewhere to start
            // — but only in shortcut mode, so switching tabs never lights a list up while focus is in the
            // search box or the tree.
            SyncShortcutHighlight();
        }

        private void ShortcutItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Template t)
                ExecutePaste(t, false);   // default paste — same as pressing the shortcut
        }
        #endregion
    }
}