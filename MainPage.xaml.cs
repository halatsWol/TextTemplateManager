using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Timers;
using TextTemplateManager.Common;
using TextTemplateManager.Data;
using TextTemplateManager.Helpers;
using TextTemplateManager.Models;
using TextTemplateManager.Services.Pasting;
using TextTemplateManager.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using UpdatePolicy = TextTemplateManager.Services.System.UpdatePolicy;
using UpdateService = TextTemplateManager.Services.System.UpdateService;
using BrowserConnector = TextTemplateManager.Services.System.BrowserConnector;
// Aliased rather than a plain using: the namespace ends in ".System", which would shadow System.* here.
using UpdateCoordinator = TextTemplateManager.Services.System.UpdateCoordinator;
using IUpdateHost = TextTemplateManager.Services.System.IUpdateHost;
using UpdateStatusKind = TextTemplateManager.Services.System.UpdateStatusKind;
using UpdateTrigger = TextTemplateManager.Services.System.UpdateTrigger;
using UpdateNote = TextTemplateManager.Services.System.UpdateNote;

namespace TextTemplateManager
{
    public sealed partial class MainPage : Page, IUpdateHost
    {
        public MainViewModel ViewModel { get; }
        private Timer _saveTimer;

        private bool _conflictUserMoved = false;   // user dragged the panel; stop auto-anchoring it
        private bool _conflictVisible = false;
        private Storyboard? _conflictStoryboard;
        private string? _dismissedNotesSignature;   // cross-area note set the user dismissed (null = none)
        private string _currentNotesSignature = "";

        // Auto-update. The state machine itself is UpdateCoordinator (no WinUI, so it is testable);
        // this class supplies it with the window, dialogs and notifications via IUpdateHost.
        private readonly UpdateService _updater = new();
        private UpdateCoordinator? _updates;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateTimer;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleTimer;
        private bool _dialogOpen;                // only one ContentDialog may be open at a time
        private Services.System.UpdateNotifier? _notifier;

        private const int IdlePollSeconds = 60;



        public MainPage()
        {
            this.InitializeComponent();

            // handledEventsToo: the TreeView marks Enter/Space handled, so a plain KeyDown handler
            // never sees them — attach here to still get folder expand/collapse on those keys.
            ItemTreeView.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(ItemTreeView_KeyDown), true);

            // Keep the conflict panel anchored to the top-right as the window sizes/resizes, until
            // the user drags it. This also corrects the very first show, which can happen before
            // the canvas has a real width.
            RootCanvas.SizeChanged += (s, e) =>
            {
                if (!_conflictUserMoved && ConflictPanel.Visibility == Visibility.Visible)
                    PositionConflictTopRight();
            };

            ViewModel = new MainViewModel();
            this.DataContext = this;

            DataNode.Instance.DataSaved += () => DispatcherQueue.TryEnqueue(ShowSaveNotification);

            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.SelectedItem))
                {
                    // Ensure the template being edited fires live shortcut validation. The one-time
                    // subscription below only covers items present at startup; sync-folder templates
                    // (and any added later) load afterward, so subscribe the selection here too.
                    if (ViewModel.SelectedItem is TextTemplateManager.Models.Template selectedTemplate)
                    {
                        selectedTemplate.PropertyChanged -= Template_PropertyChanged;
                        selectedTemplate.PropertyChanged += Template_PropertyChanged;
                    }

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        PushTemplateToEditor();
                        // Read-only synced item: block editing of the metadata fields and dim them as
                        // the cue, but keep the editor itself fully legible and interactive so its text
                        // stays selectable/copyable (setEditable(false) blocks typing/paste; the editor
                        // hides its own toolbar).
                        bool editable = ViewModel.IsSelectedEditable;
                        TemplateTitleBox.IsHitTestVisible = editable;
                        TemplateTitleBox.Opacity = editable ? 1.0 : 0.6;
                        TemplateMetaPanel.IsHitTestVisible = editable;
                        TemplateMetaPanel.Opacity = editable ? 1.0 : 0.6;
                        FolderPanel.IsHitTestVisible = editable;
                        FolderPanel.Opacity = editable ? 1.0 : 0.6;

                        // A sync-root folder's name is owned by Settings ▸ Sync — never editable here.
                        bool isSyncRoot = ViewModel.SelectedItem is Folder folder && folder.IsSyncRoot;
                        FolderTitleBox.IsReadOnly = isSyncRoot || !editable;
                        ToolTipService.SetToolTip(FolderTitleBox,
                            isSyncRoot ? "The sync folder name is set in Settings ▸ Sync" : null);

                        // Surface conflicts for the newly selected item — including sync-folder ones
                        // that loaded after startup, which the initial validation pass never saw.
                        ViewModel.ValidateAllShortcuts();
                        ShowShortcutConflicts();
                    });
                }
            };

            foreach (var t in ViewModel.AllItems
                           .SelectMany(i => Flatten(i))
                           .OfType<TextTemplateManager.Models.Template>())
            {
                t.PropertyChanged += Template_PropertyChanged;
            }

            _saveTimer = new Timer(1000) { AutoReset = false };
            _saveTimer.Elapsed += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(async () => await ViewModel.SaveCurrentStateAsync());
            };

            DispatcherQueue.TryEnqueue(() =>
            {
                ViewModel.ValidateAllShortcuts();
                ShowShortcutConflicts();
            });

            StartUpdateChecks();
        }

        // Check for updates on startup and every 10 minutes.
        private void StartUpdateChecks()
        {
            _notifier = new Services.System.UpdateNotifier(DispatcherQueue, OnNotificationAction);
            _notifier.Register();

            _updates = new UpdateCoordinator(this) { LaunchedHidden = App.IsHiddenLaunch() };

            // An install a previous session armed but never got to runs now — at launch, before any work
            // is in flight. Enqueued so it leaves the constructor's stack before shutting the app down
            // again; the rest is still armed below, since a failed launch must keep checking as usual.
            DispatcherQueue.TryEnqueue(() => _ = _updates.ResumeDeferredInstallAsync());

            _idleTimer = DispatcherQueue.CreateTimer();
            _idleTimer.Interval = TimeSpan.FromSeconds(IdlePollSeconds);
            _idleTimer.Tick += (s, e) => { if (!_updates.Busy) _ = _updates.TryAutoInstallAsync(); };
            _idleTimer.Start();

            DataNode.Instance.CurrentSettings.PropertyChanged += Settings_UpdatePrefsChanged;

            _updateTimer = DispatcherQueue.CreateTimer();
            _updateTimer.Interval = TimeSpan.FromMinutes(10);
            _updateTimer.Tick += (s, e) => _ = _updates.RunCheckAsync(UpdateTrigger.Auto);
            _updateTimer.Start();
            _ = _updates.RunCheckAsync(UpdateTrigger.Auto);
        }

        /// <summary>Called on exit — release the notification registration the unpackaged app created.</summary>
        public void ShutdownUpdates()
        {
            try { _notifier?.Unregister(); } catch { }
        }

        private void Template_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is TextTemplateManager.Models.Template t &&
                (e.PropertyName == nameof(TextTemplateManager.Models.Template.SingleKeyShortcut) ||
                 e.PropertyName == nameof(TextTemplateManager.Models.Template.MultiKeyShortcut)))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.ValidateAllShortcuts();
                    ShowShortcutConflicts();
                });
            }
        }

        // Multi-key shortcuts allow letters, digits, and the '-' / '.' separators — strip anything
        // else (whitespace, '_', other symbols). '_' is a Quick-Paste-only plaintext modifier typed
        // at paste time, never part of a stored shortcut.
        private void MultiKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            string filtered = new string(tb.Text.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '.').ToArray());
            if (tb.Text == filtered) return;
            int removed = tb.Text.Length - filtered.Length;
            int caret = tb.SelectionStart;
            tb.Text = filtered;                                       // re-enters, but now equal -> no loop
            tb.SelectionStart = Math.Clamp(caret - removed, 0, filtered.Length);
        }

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveNotifTimer;
        private Storyboard? _saveFade;

        private void ShowSaveNotification()
        {
            _saveFade?.Stop();
            SaveNotification.Opacity = 1;
            _saveNotifTimer ??= CreateSaveNotifTimer();
            _saveNotifTimer.Stop();
            _saveNotifTimer.Start();         // restart the 3s window on each save
        }

        private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateSaveNotifTimer()
        {
            var t = DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromSeconds(3);
            t.Tick += (_, _) =>
            {
                t.Stop();
                var fade = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(500)) };
                Storyboard.SetTarget(fade, SaveNotification);
                Storyboard.SetTargetProperty(fade, "Opacity");
                _saveFade = new Storyboard();
                _saveFade.Children.Add(fade);
                _saveFade.Begin();
            };
            return t;
        }

        public void ClearSearch()
        {
            if (ViewModel != null) ViewModel.SearchText = string.Empty;
        }


        #region WebView2 (TipTap) Editor

        private bool _editorInitStarted;
        private bool _editorReady;
        // Template loaded in the editor (by reference), so edits save to the right one.
        private Template? _currentEditorTemplate;

        private async void EditorWebView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_editorInitStarted) return;
            _editorInitStarted = true;
            try
            {
                await EditorWebView.EnsureCoreWebView2Async();
                var core = EditorWebView.CoreWebView2;

                core.WebMessageReceived += Editor_WebMessageReceived;

                // Standard editing context menu (cut / copy / paste / spell-check suggestions).
                // Editor_ContextMenuRequested trims it to editing items; dev tools stay off so
                // there is no "Inspect".
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.ContextMenuRequested += Editor_ContextMenuRequested;

                // Inline CSS+JS (no fetch), so WebView2's cache can't serve a stale bundle.
                string assetsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "editor");
                string html = System.IO.File.ReadAllText(System.IO.Path.Combine(assetsDir, "editor.html"));
                string css = System.IO.File.ReadAllText(System.IO.Path.Combine(assetsDir, "editor.css"));
                string js = System.IO.File.ReadAllText(System.IO.Path.Combine(assetsDir, "editor.bundle.js"));

                // Stop an accidental </script> in the bundle from closing the inline tag.
                js = js.Replace("</script", "<\\/script");

                // MatchEvaluator replacement avoids $-substitution mangling the content.
                html = System.Text.RegularExpressions.Regex.Replace(
                    html, "<link[^>]*editor\\.css[^>]*>", _ => $"<style>{css}</style>");
                html = System.Text.RegularExpressions.Regex.Replace(
                    html, "<script[^>]*editor\\.bundle\\.js[^>]*></script>", _ => $"<script>{js}</script>");

                EditorWebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Editor] init failed: {ex.Message}");
            }
        }

        // Navigation / page / developer context-menu entries to drop, leaving the editing and
        // spell-check items (whose names are dynamic and kept by default).
        private static readonly HashSet<string> _hiddenContextItems = new()
        {
            "back", "forward", "reload", "reloadFrame", "saveAs", "savePageAs", "print",
            "createQrCode", "inspectElement", "viewSource", "viewPageSource", "webCapture",
            "share", "webSelect", "translate", "saveImageAs", "copyImage", "copyImageLink",
            "openImageInNewTab", "saveLinkAs", "copyLinkToText",
        };

        // Entries WebView2 exposes with no stable Name, matched by label instead: the
        // "Writing Direction" submenu and "Send tab to your devices".
        private static readonly string[] _hiddenContextLabels = { "writing direction", "to your devices" };

        private void Editor_ContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs args)
        {
            var items = args.MenuItems;
            for (int i = items.Count - 1; i >= 0; i--)
                if (_hiddenContextItems.Contains(items[i].Name) || HasHiddenLabel(items[i].Label))
                    items.RemoveAt(i);

            // Drop separators left dangling at the menu edges after the removals.
            while (items.Count > 0 && items[0].Kind == CoreWebView2ContextMenuItemKind.Separator)
                items.RemoveAt(0);
            while (items.Count > 0 && items[^1].Kind == CoreWebView2ContextMenuItemKind.Separator)
                items.RemoveAt(items.Count - 1);
        }

        private static bool HasHiddenLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return false;
            string s = label.Replace("&", "").ToLowerInvariant();
            foreach (var frag in _hiddenContextLabels)
                if (s.Contains(frag)) return true;
            return false;
        }

        private void Editor_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string json;
            try { json = args.TryGetWebMessageAsString(); }
            catch { return; }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var type = doc.RootElement.GetProperty("type").GetString();

                if (type == "ready")
                {
                    _editorReady = true;
                    ApplyEditorTheme();
                    PushTemplateToEditor();
                }
                else if (type == "change")
                {
                    var html = doc.RootElement.TryGetProperty("html", out var h) ? h.GetString() : null;
                    OnEditorContentChanged(html ?? string.Empty);
                }
                else if (type == "openLink")
                {
                    var href = doc.RootElement.TryGetProperty("href", out var u) ? u.GetString() : null;
                    OpenExternalLink(href);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Editor] message parse error: {ex.Message}");
            }
        }

        /// <summary>Loads the selected template's HTML into the editor (migrating legacy RTF).</summary>
        private async void PushTemplateToEditor()
        {
            if (!_editorReady || EditorWebView?.CoreWebView2 == null) return;

            // Flush the previous template's debounced edits before swapping content.
            await FlushEditorAsync();

            if (ViewModel.SelectedItem is not Template t)
            {
                _currentEditorTemplate = null;
                await SetEditorHtmlAsync("<p></p>");
                return;
            }

            _currentEditorTemplate = t;
            await SetEditorHtmlAsync(GetHtmlForEditing(t));

            // Read-only for save-off synced templates.
            try
            {
                await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                    $"window.editorApi && window.editorApi.setEditable({(ViewModel.IsSelectedEditable ? "true" : "false")})");
            }
            catch { }
        }

        /// <summary>Reads the editor's HTML and saves it to the template it belongs to.</summary>
        private async Task FlushEditorAsync()
        {
            var t = _currentEditorTemplate;
            if (t == null || EditorWebView?.CoreWebView2 == null) return;

            try
            {
                // ExecuteScriptAsync returns the result JSON-encoded (a quoted string).
                string result = await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.editorApi.getContent()");
                string html = System.Text.Json.JsonSerializer.Deserialize<string>(result) ?? string.Empty;
                if (t.Content != html)
                {
                    t.Content = html;
                    _saveTimer.Stop();
                    _saveTimer.Start();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Editor] flush failed: {ex.Message}");
            }
        }

        /// <summary>Template content as HTML; legacy RTF is converted once and written back.</summary>
        private static string GetHtmlForEditing(Template t)
        {
            string content = (t.Content ?? string.Empty).TrimEnd('\0');

            if (content.TrimStart().StartsWith("{\\rtf1", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string html = RtfPipe.Rtf.ToHtml(content);
                    t.Content = html; // migrate: store HTML from now on
                    return html;
                }
                catch
                {
                    return "<p></p>";
                }
            }

            return string.IsNullOrWhiteSpace(content) ? "<p></p>" : content;
        }

        private async Task SetEditorHtmlAsync(string html)
        {
            try
            {
                string arg = System.Text.Json.JsonSerializer.Serialize(html);
                await EditorWebView.CoreWebView2.ExecuteScriptAsync($"window.editorApi.setContent({arg})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Editor] setContent failed: {ex.Message}");
            }
        }

        private void OnEditorContentChanged(string html)
        {
            // The message reflects whatever is currently loaded in the editor, so save it to
            // that template (tracked by reference), not necessarily the current tree selection.
            var t = _currentEditorTemplate;
            if (t == null) return;

            if (t.Content != html)
            {
                t.Content = html;
                _saveTimer.Stop();
                _saveTimer.Start();
            }
        }

        private void ApplyEditorTheme()
        {
            if (EditorWebView?.CoreWebView2 == null) return;
            bool dark = ActualTheme == ElementTheme.Dark;
            _ = EditorWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.editorApi && window.editorApi.setTheme({(dark ? "true" : "false")})");
        }

        #endregion

        private static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key, Windows.UI.Color fallback)
            => Application.Current.Resources.TryGetValue(key, out var v) && v is Microsoft.UI.Xaml.Media.Brush b
                ? b
                : new SolidColorBrush(fallback);

        #region Menu Handlers

        private void MainHelpButton_Click(object sender, RoutedEventArgs e) => MainHelpTip.IsOpen = true;

        private void AddTemplate_Click(object sender, RoutedEventArgs e) => ViewModel.AddTemplateCommand.Execute(null);
        private void AddFolder_Click(object sender, RoutedEventArgs e) => ViewModel.AddFolderCommand.Execute(null);
        private async void DeleteItem_Click(object sender, RoutedEventArgs e) => await ConfirmAndDeleteAsync();

        /// <summary>Tree context menu: Delete for any item; Duplicate/Copy/Copy As for templates;
        /// Export for non-root folders.</summary>
        private void Tree_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not BaseItem item) return;

            ViewModel.SelectedItem = item;   // so the panel and delete act on it

            var flyout = new MenuFlyout();

            if (item is Template t)
            {
                var dup = new MenuFlyoutItem { Text = "Duplicate", Icon = new SymbolIcon(Symbol.Copy) };
                dup.Click += (_, _) => ViewModel.CloneItemCommand.Execute(null);
                flyout.Items.Add(dup);

                var copy = new MenuFlyoutItem { Text = $"Copy ({PasteModeLabel.For(t.DefaultPasteMode)})" };
                copy.Click += (_, _) => _ = PasteService.CopyToClipboardAsync(t.Content, t.DefaultPasteMode);
                flyout.Items.Add(copy);

                var copyAs = new MenuFlyoutSubItem { Text = "Copy As" };
                foreach (PasteMode mode in ViewModel.PasteModes)
                {
                    var captured = mode;
                    var mi = new MenuFlyoutItem { Text = PasteModeLabel.For(mode) };
                    mi.Click += (_, _) => _ = PasteService.CopyToClipboardAsync(t.Content, captured);
                    copyAs.Items.Add(mi);
                }
                flyout.Items.Add(copyAs);
            }
            else if (item is Folder folder && !folder.IsSyncRoot)   // not a pinned sync root
            {
                var export = new MenuFlyoutItem { Text = "Export…", Icon = new SymbolIcon(Symbol.Save) };
                export.Click += async (_, _) => await ViewModel.ExportFolderAsync(folder);
                flyout.Items.Add(export);
            }

            // Move to Root — for a nested, editable item (a reliable alternative to dragging it out).
            if (!item.IsSyncRoot && !ViewModel.IsReadOnly(item)
                && ViewModel.FindParent(ViewModel.AllItems, item) != null)
            {
                var toRoot = new MenuFlyoutItem { Text = "Move to Root" };
                toRoot.Click += (_, _) => ViewModel.MoveToRoot(item);
                flyout.Items.Add(toRoot);
            }

            // Sync-folder roots are managed in Settings ▸ Sync, and save-off (read-only) sync items
            // can't be modified — no Delete for either.
            if (!item.IsSyncRoot && !ViewModel.IsReadOnly(item))
            {
                if (flyout.Items.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
                var del = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
                del.Click += async (_, _) => await ConfirmAndDeleteAsync();
                flyout.Items.Add(del);
            }

            if (flyout.Items.Count > 0)
                flyout.ShowAt(fe, new FlyoutShowOptions { Position = e.GetPosition(fe) });
        }

        /// <summary>Delete the selected item; confirm for templates and non-empty folders, but not
        /// for an empty folder.</summary>
        private async Task ConfirmAndDeleteAsync()
        {
            if (ViewModel.SelectedItem is not BaseItem item) return;
            if (item.IsSyncRoot || ViewModel.IsReadOnly(item)) return;   // sync roots via Settings; save-off is read-only

            string? message = null;

            if (item is Folder folder && folder.Children.Count > 0)
            {
                int childCount = Flatten(folder).Count() - 1; // exclude the folder itself
                message = $"Delete the folder \"{item.Title}\" and its {childCount} item(s)?\n\n" +
                          "All items inside this folder will be deleted as well. This cannot be undone.";
            }
            else if (item is Template)
            {
                message = $"Delete the template \"{item.Title}\"? This cannot be undone.";
            }
            // else: empty folder -> no confirmation needed.

            if (message != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Confirm deletion",
                    Content = message,
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };

                if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
            }

            ViewModel.DeleteItemCommand.Execute(null);
        }
        private void LoadBackup_Click(object sender, RoutedEventArgs e) => ViewModel.LoadBackupCommand.Execute(null);
        private void SaveBackup_Click(object sender, RoutedEventArgs e) => ViewModel.SaveBackupCommand.Execute(null);

        private void OpenPreferences_Click(object sender, RoutedEventArgs e) => ShowSettings();

        // ---- In-window settings view ----

        private void ShowSettings()
        {
            SettingsOverlay.Visibility = Visibility.Visible;
            var general = SettingsNav.MenuItems[0];
            if (ReferenceEquals(SettingsNav.SelectedItem, general))
                NavigateSettings("General");                 // already selected -> navigate manually
            else
                SettingsNav.SelectedItem = general;          // fires SelectionChanged -> navigate
        }

        private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item)
                NavigateSettings(item.Tag?.ToString());
        }

        private void NavigateSettings(string? tag)
        {
            if (tag == "General")
                SettingsFrame.Navigate(typeof(GeneralSettingsPage), DataNode.Instance.CurrentSettings);
            else if (tag == "Sync")
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                SettingsFrame.Navigate(typeof(SyncSettingsPage), hwnd.ToInt64());
            }
        }

        private void SettingsNav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => CloseSettings();

        /// <summary>Leaves the Settings view if it is open. Called when the window is hidden to the tray so
        /// that reopening lands on the template list, rather than resuming wherever Settings was left —
        /// and so its normal teardown (persist, reload the tree, refresh conflicts) still runs.</summary>
        public void CloseSettingsIfOpen()
        {
            if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings();
        }

        private async void CloseSettings()
        {
            // Persist app settings (autostart is registry-only now — see StartupManager).
            await StorageService.SaveSettingsAsync(DataNode.Instance.CurrentSettings);

            SettingsOverlay.Visibility = Visibility.Collapsed;
            ViewModel.ReloadTree();   // full rebuild so sync-folder order changes are reflected
            ShowShortcutConflicts();  // reflect the "hide cross-area warnings" setting
        }

        // ---- File association (.ttmdata) ----

        /// <summary>Opens the in-window Settings directly on the Sync page.</summary>
        public void OpenSyncSettings()
        {
            SettingsOverlay.Visibility = Visibility.Visible;
            var syncItem = SettingsNav.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(i => (i.Tag as string) == "Sync");
            if (syncItem == null) return;
            if (ReferenceEquals(SettingsNav.SelectedItem, syncItem))
                NavigateSettings("Sync");                // already selected -> navigate manually
            else
                SettingsNav.SelectedItem = syncItem;     // fires SelectionChanged -> navigate
        }

        /// <summary>Handles a .ttmdata opened via the file association: links it as a sync source
        /// (unless it's this app's own data file or already linked), then shows Sync settings.</summary>
        public async void HandleOpenTtmDataFile(string path)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch { return; }
            if (!System.IO.File.Exists(full)) return;

            // The app's own primary data file is never linked as a sync source.
            if (string.Equals(full, StorageService.GetDataPath(), StringComparison.OrdinalIgnoreCase))
            {
                OpenSyncSettings();
                await ShowMessageAsync("Not added as a sync source",
                    "The added file is this app's own data file, so it can't be linked as a sync source.");
                return;
            }

            var sync = DataNode.Instance.CurrentSyncSettings;
            bool already = sync.Sources.Any(s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase));
            if (!already)
            {
                sync.Sources.Add(new SyncSource
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(full),
                    Path = full,
                    IsActive = true,
                    AllowSave = false,
                });
                await DataNode.Instance.SaveSyncSettingsAsync();
                await DataNode.Instance.ReapplySyncAsync();
            }

            OpenSyncSettings();
        }

        private const string GitHubUrl = "https://github.com/halatsWol/TextTemplateManager";
        private const string ContactEmail = "contact@kmarflow.com";

        private void GoToGitHub_Click(object sender, RoutedEventArgs e) => OpenExternal(GitHubUrl);

        private async void OpenManual_Click(object sender, RoutedEventArgs e)
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Manual.pdf");
            if (System.IO.File.Exists(path)) OpenExternal(path);
            else await ShowMessageAsync("Manual unavailable",
                "The manual PDF was not found next to the app. It is generated at build time.");
        }

        // Opens a URL or local file with the shell's default handler.
        private static void OpenExternal(string target)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Help] open failed: {ex.Message}"); }
        }

        // Opens an editor link (Ctrl+click) in the system browser. Restricted to web/mail/tel
        // schemes so a template can't launch file:// or a custom protocol handler via a click.
        private static void OpenExternalLink(string? href)
        {
            if (string.IsNullOrWhiteSpace(href)) return;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme is "http" or "https" or "mailto" or "tel" or "ftp" or "ftps")
                OpenExternal(uri.AbsoluteUri);
        }

        // The release version, taken from the tag-driven InformationalVersion (e.g. "0.9.3"),
        // falling back to the numeric assembly version for local dev builds.
        private static string AppVersion()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');   // strip any +<git-hash> the SDK may append
                return plus >= 0 ? info[..plus] : info;
            }
            var v = asm.GetName().Version ?? new Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }

        // The release date baked into the assembly at build time (yyyy-MM-dd), or "" for a dev build.
        private static string ReleaseDate()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var a in asm.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                if (a is System.Reflection.AssemblyMetadataAttribute m && m.Key == "ReleaseDate" && !string.IsNullOrWhiteSpace(m.Value))
                    return m.Value!;
            return "";
        }

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            var secondary = ThemeBrush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray);

            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = "Text Template Manager", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"Version {AppVersion()}", Foreground = secondary });
            string releaseDate = ReleaseDate();
            panel.Children.Add(new TextBlock { Text = releaseDate.Length > 0 ? $"Released {releaseDate}" : "Released: dev build", Foreground = secondary });
            panel.Children.Add(new TextBlock { Text = $"Connector API protocol {BrowserConnector.ProtocolVersion}", Foreground = secondary });
            panel.Children.Add(new TextBlock { Text = "Marflow Software", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(new TextBlock { Text = $"© {DateTime.Now.Year} Marflow Software", Foreground = secondary });
            panel.Children.Add(new TextBlock { Text = "A hotkey-driven text-template paste tool.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(new HyperlinkButton { Content = "Project on GitHub", NavigateUri = new Uri(GitHubUrl), Padding = new Thickness(0) });

            // Email: try the mail app, but always copy to the clipboard (a mailto: link does nothing
            // when no default mail app is registered). Inline hint instead of a dialog, since a
            // second ContentDialog can't open while About is showing.
            var emailLink = new HyperlinkButton { Content = ContactEmail, Padding = new Thickness(0) };
            var copiedHint = new TextBlock
            {
                Text = "Address copied to clipboard",
                FontSize = 11,
                Foreground = secondary,
                Visibility = Visibility.Collapsed
            };
            emailLink.Click += async (_, _) =>
            {
                var dp = new DataPackage();
                dp.SetText(ContactEmail);
                Clipboard.SetContent(dp);
                copiedHint.Visibility = Visibility.Visible;
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri($"mailto:{ContactEmail}")); } catch { }
            };
            panel.Children.Add(emailLink);
            panel.Children.Add(copiedHint);

            var dialog = new ContentDialog
            {
                Title = "About",
                Content = panel,
                PrimaryButtonText = "View License",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
                await ShowLicenseAsync();
        }

        private async Task ShowLicenseAsync()
        {
            string text;
            try { text = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "LICENSE")); }
            catch { text = "License file not found."; }

            var scroll = new ScrollViewer
            {
                MaxHeight = 440,
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12
                }
            };
            await ShowDialogAsync(new ContentDialog
            {
                Title = "License",
                Content = scroll,
                CloseButtonText = "Close",
            });
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            await ShowDialogAsync(new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
            });
        }

        /// <summary>The single place a ContentDialog is opened. Only one may be open at a time — a second
        /// ShowAsync throws — and update prompts now surface on their own schedule, so they could otherwise
        /// collide with whatever dialog the user already had open. Returns None when it can't be shown.</summary>
        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            if (this.XamlRoot == null || _dialogOpen) return ContentDialogResult.None;
            _dialogOpen = true;
            try
            {
                dialog.XamlRoot = this.XamlRoot;
                return await dialog.ShowAsync();
            }
            catch { return ContentDialogResult.None; }
            finally { _dialogOpen = false; }
        }

        // ---- Auto-update ----
        //
        // The decisions live in UpdateCoordinator, which is free of WinUI so it can be tested. This class
        // is the adapter that lends it a window, dialogs, notifications and the Win32 probes.

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
            await _updates!.RunCheckAsync(UpdateTrigger.User);

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_updates is { ReadyInstallerPath: string path, ReadyVersionLabel: string label })
                await _updates.PromptAndInstallAsync(label, path, _updates.AutoInstallBlocked);
        }

        // "Update available" (shown only when auto-download is off): download now, then offer to install.
        private async void UpdateAvailableButton_Click(object sender, RoutedEventArgs e) =>
            await _updates!.RunCheckAsync(UpdateTrigger.User);

        private void Settings_UpdatePrefsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppSettings.AutoInstallUpdates))
                _ = _updates!.OnAutoInstallSettingChangedAsync();
        }

        // A toast button came back while the app is running.
        private void OnNotificationAction(string action) => _ = _updates!.OnNotificationActionAsync(action);

        // Is the main window actually the foreground, visible window right now? (A hidden/tray window is not.)
        private static bool ForegroundNow()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                return WindowHelper.IsWindowVisible(hwnd) && WindowHelper.GetForegroundWindow() == hwnd;
            }
            catch { return false; }
        }

        // Is the window on screen at all? Decides whether a dialog would actually be seen — showing one on a
        // window hidden in the tray leaves the user staring at nothing while the app waits for an answer.
        private static bool WindowVisible()
        {
            try { return WindowHelper.IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)); }
            catch { return false; }
        }

        // Reflect the update state in the top-right strip and the "Check for Updates" menu dot.
        private void SetUpdateStatus(UpdateStatusKind kind, string busyText)
        {
            UpdateBusyIndicator.Visibility = kind == UpdateStatusKind.Downloading ? Visibility.Visible : Visibility.Collapsed;
            if (kind == UpdateStatusKind.Downloading) UpdateBusyText.Text = busyText;
            UpdateAvailableButton.Visibility = kind == UpdateStatusKind.Available ? Visibility.Visible : Visibility.Collapsed;
            UpdateButton.Visibility = kind == UpdateStatusKind.Ready ? Visibility.Visible : Visibility.Collapsed;

            // Keep the standard refresh glyph and mark it when an update is waiting. MenuFlyoutItem.Icon
            // takes a single IconElement, so a badge dot cannot simply be layered on top of the glyph —
            // the whole thing is drawn as one geometry instead (see UpdateMenuIcon).
            bool known = kind is UpdateStatusKind.Available or UpdateStatusKind.Ready;
            CheckForUpdatesMenuItem.Icon = UpdateMenuIcon(known);
        }

        private static readonly Windows.UI.Color UpdateAccentColor =
            Windows.UI.Color.FromArgb(0xFF, 0xC8, 0xA0, 0x00);

        /// <summary>Icon for "Check for Updates": the stock refresh symbol normally, and — when an update
        /// is waiting — a refresh ring in the ordinary icon colour with a small accent dot in its
        /// top-right corner.
        ///
        /// Rendered from SVG because the badged version needs two colours, and every IconElement paints
        /// with a single Foreground; MenuFlyoutItem.Icon likewise takes one element, so there is no
        /// container to layer a badge into. The ring colour is resolved from the theme at build time.</summary>
        private static IconElement UpdateMenuIcon(bool updateWaiting)
        {
            if (!updateWaiting) return new SymbolIcon(Symbol.Refresh);

            var ink = ThemeBrush("TextFillColorPrimaryBrush", Microsoft.UI.Colors.Black) is SolidColorBrush b
                ? b.Color
                : Microsoft.UI.Colors.Black;

            // 16x16 box, ring centred (7,9): the outer arc runs from 12 o'clock anticlockwise round to
            // 3 o'clock and back along the inner radius, leaving the top-right free for the badge.
            string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'>" +
                $"<path d='M 7 4 A 5 5 0 1 0 12 9 L 10.4 9 A 3.4 3.4 0 1 1 7 5.6 Z' fill='{Hex(ink)}'/>" +
                $"<path d='M 5.6 2.2 L 5.6 6.4 L 9.2 4.3 Z' fill='{Hex(ink)}'/>" +
                $"<circle cx='13' cy='3' r='2.3' fill='{Hex(UpdateAccentColor)}'/>" +
                "</svg>";

            var source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource();
            _ = LoadSvgAsync(source, svg);
            return new ImageIcon { Source = source, Width = 16, Height = 16 };
        }

        private static string Hex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static async Task LoadSvgAsync(Microsoft.UI.Xaml.Media.Imaging.SvgImageSource target, string svg)
        {
            try
            {
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0));
                writer.WriteString(svg);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
                stream.Seek(0);
                await target.SetSourceAsync(stream);
            }
            catch { /* the icon is cosmetic — never break the menu over it */ }
        }

        // ---- IUpdateHost: everything the coordinator needs from the running app ----

        bool IUpdateHost.UpdatesAllowed => UpdatePolicy.UpdatesAllowed;
        bool IUpdateHost.BetaAllowed => UpdatePolicy.BetaAllowed;
        bool IUpdateHost.AutoCheckUpdates => DataNode.Instance.CurrentSettings.AutoCheckUpdates;
        bool IUpdateHost.AutoInstallUpdates => DataNode.Instance.CurrentSettings.AutoInstallUpdates;
        bool IUpdateHost.AllowBetaUpdates => DataNode.Instance.CurrentSettings.AllowBetaUpdates;

        Task<UpdateService.UpdateInfo?> IUpdateHost.CheckAsync(bool allowBeta) => _updater.CheckAsync(allowBeta);
        Task<string?> IUpdateHost.EnsureDownloadedAsync(UpdateService.UpdateInfo info) => _updater.EnsureDownloadedAsync(info);

        string IUpdateHost.InstallerDir => StorageService.GetInstallerDir();
        string IUpdateHost.InstalledVersion => UpdateService.InstalledVersionString();
        string IUpdateHost.AppVersion => AppVersion();

        void IUpdateHost.CleanInstallerDir(string? keep) =>
            UpdateService.CleanInstallerDir(StorageService.GetInstallerDir(), keep);

        bool IUpdateHost.HasUsableInstaller(string path)
        {
            try { return System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 0; }
            catch { return false; }
        }

        bool IUpdateHost.WindowVisible => WindowVisible();
        bool IUpdateHost.WindowForeground => ForegroundNow();
        bool IUpdateHost.PasteWindowVisible => (Application.Current as App)?.PasteWindowVisible == true;
        uint IUpdateHost.IdleSeconds => WindowHelper.GetIdleSeconds();
        bool IUpdateHost.WritesQuiet => StorageService.WritesQuiet();

        void IUpdateHost.SetStatus(UpdateStatusKind kind, string busyText) =>
            SetUpdateStatus(kind, string.IsNullOrEmpty(busyText) ? "Downloading update…" : busyText);

        void IUpdateHost.SetBusy(bool busy) => UpdateAvailableButton.IsEnabled = !busy;

        void IUpdateHost.FlashTaskbar() =>
            WindowHelper.FlashTaskbar(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        // The effective answer to "may I show an update notification?" — a future parent
        // "show notifications" switch folds in here, leaving the coordinator unchanged.
        bool IUpdateHost.NotificationsEnabled => DataNode.Instance.CurrentSettings.ShowUpdateNotifications;
        bool IUpdateHost.NotificationsAvailable => _notifier?.IsAvailable == true;

        void IUpdateHost.ShowToast(UpdateNote note, string versionLabel)
        {
            switch (note)
            {
                case UpdateNote.DownloadAvailable: _notifier?.ShowDownloadAvailable(versionLabel); break;
                case UpdateNote.InstallReady: _notifier?.ShowInstallReady(versionLabel); break;
                case UpdateNote.AutoInstallFailed: _notifier?.ShowAutoInstallFailed(versionLabel); break;
            }
        }

        void IUpdateHost.ShowInstallingToast(string versionLabel) => _notifier?.ShowInstalling(versionLabel);

        void IUpdateHost.ShowTrayBalloon(string title, string message) =>
            (Application.Current as App)?.ShowTrayBalloon(title, message);

        Task IUpdateHost.ClearNotificationsAsync() => _notifier?.ClearAsync() ?? Task.CompletedTask;

        void IUpdateHost.SurfaceMainWindow() => App.SurfaceMainWindow();

        async Task<bool> IUpdateHost.PromptInstallAsync(string versionLabel, bool autoFailed)
        {
            var dialog = new ContentDialog
            {
                Title = autoFailed ? "Update needs your attention" : "Update available",
                Content = autoFailed
                    ? $"Version {versionLabel} couldn't be installed automatically. Install it now, or download it yourself from GitHub."
                    : $"Version {versionLabel} is ready to install. The app will close, update, and reopen.",
                PrimaryButtonText = "Install now",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
            };
            return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
        }

        Task IUpdateHost.ShowMessageAsync(string title, string message) => ShowMessageAsync(title, message);

        async Task IUpdateHost.PersistPendingWorkAsync()
        {
            // Persist first, then hand off to the silent installer and exit so files aren't locked.
            await FlushEditorAsync();
            _saveTimer?.Stop();
            await ViewModel.SaveCurrentStateAsync();
        }

        async Task IUpdateHost.DrainWritesAsync()
        {
            // Capped — a source that is permanently stuck must not block the update forever.
            for (int i = 0; i < 25 && StorageService.WritesInFlight; i++)
                await Task.Delay(100);
        }

        bool IUpdateHost.LaunchInstaller(string path, bool relaunchHidden) =>
            UpdateService.LaunchInstaller(path, relaunchHidden);

        void IUpdateHost.Shutdown() => (Application.Current as App)?.Shutdown();

        private async void Exit_Click(object sender, RoutedEventArgs e)
        {
            await FlushEditorAsync();   // capture latest edits before saving
            _saveTimer?.Stop();
            await ViewModel.SaveCurrentStateAsync();
            (Application.Current as App)?.Shutdown();
        }

        #endregion

        #region TreeView Logic

        private void ItemTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is BaseItem selected)
            {
                ViewModel.SelectedItem = selected;
                PushTemplateToEditor();
            }
        }

        // Click in empty tree space -> clear the selection (so new items go to the root).
        private void ItemTreeView_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (FindItemFromSource(e.OriginalSource) == null)
                ViewModel.SelectedItem = null;
        }

        private async void ItemTreeView_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // ESC: clear the selection (so new items go to the root).
            if (e.Key == VirtualKey.Escape && ViewModel.SelectedItem != null)
            {
                e.Handled = true;
                ViewModel.SelectedItem = null;
                return;
            }

            // DEL: delete selected (same confirm as the button).
            if (e.Key == VirtualKey.Delete && ViewModel.SelectedItem != null)
            {
                e.Handled = true;
                await ConfirmAndDeleteAsync();
                return;
            }

            // Ctrl+C: copy the template in its default paste mode.
            if (e.Key == VirtualKey.C && IsCtrlDown() && ViewModel.SelectedItem is Template t)
            {
                e.Handled = true;
                await PasteService.CopyToClipboardAsync(t.Content, t.DefaultPasteMode);
            }

            // Enter/Space on a folder: expand/collapse it.
            if ((e.Key is VirtualKey.Enter or VirtualKey.Space) && ViewModel.SelectedItem is Folder f)
            {
                e.Handled = true;
                f.IsExpanded = !f.IsExpanded;
            }
        }

        private static bool IsCtrlDown()
            => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down)
               == CoreVirtualKeyStates.Down;

        // Ctrl+F focuses the search box from anywhere in the window (page-level accelerator).
        private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            SearchBox.Focus(FocusState.Programmatic);
        }

        private void ItemTreeView_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Move;

            // Reject drops onto a read-only sync folder.
            if (FindItemFromSource(e.OriginalSource) is BaseItem target && ViewModel.IsReadOnly(target))
                e.AcceptedOperation = DataPackageOperation.None;
        }

        private static BaseItem? FindItemFromSource(object source)
        {
            var el = source as DependencyObject;
            while (el != null)
            {
                if (el is FrameworkElement fe && fe.DataContext is BaseItem item) return item;
                el = VisualTreeHelper.GetParent(el);
            }
            return null;
        }

        private void ItemTreeView_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
        {
            // Pinned sync roots and read-only items can't be dragged.
            ViewModel.ClearDragOrigins();
            foreach (var obj in args.Items)
            {
                if (obj is BaseItem item)
                {
                    if (item.IsSyncRoot || ViewModel.IsReadOnly(item)) { args.Cancel = true; return; }
                    ViewModel.CaptureDragOrigin(item);
                }
            }

            ViewModel.BeginDrag();   // suppress auto-save mid-drag; saved once on completion
        }

        private async void ItemTreeView_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
        {
            var movedItem = args.Items.FirstOrDefault() as BaseItem;
            if (movedItem == null)
            {
                // Nothing moved: still clear the suppression flag we set on start.
                ViewModel.EndDrag();
                return;
            }
            await ViewModel.SyncMasterAfterDragAsync();

            // A boundary-crossing move may have cleared/renamed shortcuts (area rules) — refresh
            // the conflict flags and the floating panel.
            ViewModel.ValidateAllShortcuts();
            ShowShortcutConflicts();
        }

        #endregion

        #region Shortcut Conflicts Warning

        // Drag support
        private bool _isDragging = false;
        private Windows.Foundation.Point _dragStartPoint;

        private void ConflictPanel_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Pressing the dismiss button must not start a drag.
            if (ConflictDismissButton != null && IsWithin(e.OriginalSource as DependencyObject, ConflictDismissButton))
                return;
            _isDragging = true;
            _dragStartPoint = e.GetCurrentPoint(RootCanvas).Position;
            ConflictPanel.CapturePointer(e.Pointer);
        }

        private void ConflictDismiss_Click(object sender, RoutedEventArgs e)
        {
            _dismissedNotesSignature = _currentNotesSignature;   // keep hidden until the note set changes
            ConflictPanel.Visibility = Visibility.Collapsed;
            _conflictVisible = false;
        }

        private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (node == ancestor) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        private void ConflictPanel_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isDragging = false;
            ConflictPanel.ReleasePointerCapture(e.Pointer);
        }

        private void ConflictPanel_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            _conflictUserMoved = true;   // once dragged, stop auto-anchoring to the top-right

            var currentPoint = e.GetCurrentPoint(RootCanvas).Position;
            double offsetX = currentPoint.X - _dragStartPoint.X;
            double offsetY = currentPoint.Y - _dragStartPoint.Y;

            double newLeft = Canvas.GetLeft(ConflictPanel) + offsetX;
            double newTop = Canvas.GetTop(ConflictPanel) + offsetY;

            // Enforce bounds
            newLeft = Math.Max(0, Math.Min(newLeft, RootCanvas.ActualWidth - ConflictPanel.ActualWidth));
            newTop = Math.Max(0, Math.Min(newTop, RootCanvas.ActualHeight - ConflictPanel.ActualHeight));

            Canvas.SetLeft(ConflictPanel, newLeft);
            Canvas.SetTop(ConflictPanel, newTop);

            _dragStartPoint = currentPoint;
        }




        // Places the conflict panel in the top-right of the canvas. Skips when the layout isn't
        // ready yet (width 0) — RootCanvas.SizeChanged re-runs this once it is.
        private void PositionConflictTopRight()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RootCanvas.UpdateLayout();
                ConflictPanel.UpdateLayout();
                double avail = RootCanvas.ActualWidth;
                double panelW = ConflictPanel.ActualWidth;
                if (avail <= 0 || panelW <= 0) return;
                Canvas.SetLeft(ConflictPanel, Math.Max(0, avail - panelW - 16));
                Canvas.SetTop(ConflictPanel, 52);   // clear the menu bar / top-right buttons
            });
        }

        private void ShowShortcutConflicts()
        {
            if (ViewModel == null) return;

            ConflictStack.Children.Clear();

            var allTemplates = ViewModel.AllItems.SelectMany(i => Flatten(i)).OfType<TextTemplateManager.Models.Template>().ToList();

            var singleConflicts = allTemplates
                .Where(t => t.HasSingleKeyConflict)
                .GroupBy(t => t.SingleKeyShortcut)
                .ToList();

            var multiConflicts = allTemplates
                .Where(t => t.HasMultiKeyConflict)
                .GroupBy(t => t.MultiKeyShortcut)
                .ToList();

            // Cross-area duplicates: informational notes, not blocking conflicts.
            var crossAreaNotes = allTemplates
                .Where(t => t.HasSingleKeyCrossAreaWarning)
                .GroupBy(t => t.SingleKeyShortcut)
                .ToList();

            var multiCrossAreaNotes = allTemplates
                .Where(t => t.HasMultiKeyCrossAreaWarning)
                .GroupBy(t => t.MultiKeyShortcut)
                .ToList();

            // Setting: hide the dismissible cross-area notices (blocking same-area conflicts still show).
            if (DataNode.Instance.CurrentSettings.HideCrossAreaShortcutWarnings)
            {
                crossAreaNotes.Clear();
                multiCrossAreaNotes.Clear();
            }

            bool hasErrors = singleConflicts.Any() || multiConflicts.Any();
            bool hasNotes = crossAreaNotes.Any() || multiCrossAreaNotes.Any();

            if (!hasErrors && !hasNotes)
            {
                _dismissedNotesSignature = null;
                ConflictPanel.Visibility = Visibility.Collapsed;
                _conflictVisible = false;
                return;
            }

            // Same-area conflicts (local↔local or within one sync folder) must be resolved, so
            // they reset any prior dismissal and the panel is non-dismissable. Only when the panel
            // holds nothing but cross-area (sync↔local) notes may it be dismissed.
            if (hasErrors) _dismissedNotesSignature = null;
            bool onlyNotes = !hasErrors && hasNotes;
            _currentNotesSignature = string.Join("|",
                crossAreaNotes.SelectMany(g => g.Select(t => "S:" + g.Key + "~" + t.Title))
                    .Concat(multiCrossAreaNotes.SelectMany(g => g.Select(t => "M:" + g.Key + "~" + t.Title)))
                    .OrderBy(s => s, StringComparer.Ordinal));

            // A dismissed note set stays hidden until it changes (or an error appears).
            if (onlyNotes && _dismissedNotesSignature == _currentNotesSignature)
            {
                ConflictPanel.Visibility = Visibility.Collapsed;
                _conflictVisible = false;
                return;
            }

            ConflictDismissButton.Visibility = onlyNotes ? Visibility.Visible : Visibility.Collapsed;
            ConflictPanel.Visibility = Visibility.Visible;

            // Anchor to the top-right whenever shown, unless the user has dragged it elsewhere.
            if (!_conflictUserMoved) PositionConflictTopRight();

            // Fade in safely
            if (!_conflictVisible)
            {
                if (_conflictStoryboard == null)
                {
                    // Try to grab it from resources
                    if (Resources.ContainsKey("ConflictFadeIn"))
                    {
                        _conflictStoryboard = (Storyboard)Resources["ConflictFadeIn"];
                    }
                }

                if (_conflictStoryboard != null && ConflictPanel != null)
                {
                    _conflictStoryboard.Stop();
                    Storyboard.SetTarget(_conflictStoryboard, ConflictPanel);
                    _conflictStoryboard.Begin();
                    _conflictVisible = true;
                }
            }




            void AddGroup(string title, List<IGrouping<string, TextTemplateManager.Models.Template>> groups)
            {
                if (!groups.Any()) return;

                ConflictStack.Children.Add(new TextBlock
                {
                    Text = title,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Margin = new Thickness(0, 5, 0, 5)
                });

                foreach (var g in groups)
                {
                    ConflictStack.Children.Add(new TextBlock
                    {
                        Text = $"Shortcut: {g.Key}",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Margin = new Thickness(10, 2, 0, 0)
                    });

                    foreach (var t in g)
                    {
                        ConflictStack.Children.Add(new TextBlock
                        {
                            Text = $"- {t.Title}",
                            Margin = new Thickness(20, 0, 0, 0)
                        });
                    }
                }
            }

            AddGroup("Single-Key Conflicts", singleConflicts);
            AddGroup("Multi-Key Conflicts", multiConflicts);

            // Informational (non-blocking) notes for the same key used across areas.
            void AddNotes(string header, List<IGrouping<string, TextTemplateManager.Models.Template>> groups)
            {
                if (!groups.Any()) return;

                ConflictStack.Children.Add(new TextBlock
                {
                    Text = header,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray),
                    Margin = new Thickness(0, 8, 0, 4)
                });

                foreach (var g in groups)
                    foreach (var t in g)
                        ConflictStack.Children.Add(new TextBlock
                        {
                            Text = $"- {g.Key}: {t.Title}",
                            Foreground = ThemeBrush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray),
                            Margin = new Thickness(20, 0, 0, 0)
                        });
            }

            AddNotes("Note — same single key in different areas (allowed; local wins, then sync order):", crossAreaNotes);
            AddNotes("Note — same multi-key in different areas (allowed; local wins, then sync order):", multiCrossAreaNotes);
        }


        #endregion

        #region Helpers

        private IEnumerable<BaseItem> Flatten(BaseItem root)
        {
            yield return root;
            foreach (var child in root.Children.SelectMany(c => Flatten(c)))
                yield return child;
        }

        #endregion
    }
}
