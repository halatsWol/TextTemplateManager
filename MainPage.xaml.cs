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

namespace TextTemplateManager
{
    public sealed partial class MainPage : Page
    {
        public MainViewModel ViewModel { get; }
        private Timer _saveTimer;

        private bool _conflictUserMoved = false;   // user dragged the panel; stop auto-anchoring it
        private bool _conflictVisible = false;
        private Storyboard? _conflictStoryboard;
        private string? _dismissedNotesSignature;   // cross-area note set the user dismissed (null = none)
        private string _currentNotesSignature = "";

        // Auto-update
        private readonly UpdateService _updater = new();
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateTimer;
        private string? _readyInstallerPath;
        private string? _readyVersionLabel;  // release tag shown in the install prompt (e.g. "0.9.6-beta")
        private Version? _promptedVersion;   // last version we showed the modal prompt for
        private bool _updateBusy;



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
                        // Dim + block interaction for read-only synced items — covers the editor
                        // and the folder-title box. (Grid is a Panel, so no IsEnabled.)
                        bool editable = ViewModel.IsSelectedEditable;
                        TemplatePanel.IsHitTestVisible = editable;
                        TemplatePanel.Opacity = editable ? 1.0 : 0.6;
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
            _updateTimer = DispatcherQueue.CreateTimer();
            _updateTimer.Interval = TimeSpan.FromMinutes(10);
            _updateTimer.Tick += (s, e) => _ = RunUpdateCheckAsync(manual: false);
            _updateTimer.Start();
            _ = RunUpdateCheckAsync(manual: false);
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

            // Sync-folder roots are managed in Settings ▸ Sync — no Delete here.
            if (!item.IsSyncRoot)
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
            if (item.IsSyncRoot) return;   // sync folders are removed via Settings ▸ Sync

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
                    XamlRoot = this.XamlRoot
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
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

        private async void CloseSettings()
        {
            // Persist app settings + reconcile the OS autostart entry (the old window's Save step).
            await StorageService.SaveSettingsAsync(DataNode.Instance.CurrentSettings);
            Services.System.StartupManager.SetEnabled(DataNode.Instance.CurrentSettings.RunAtStartup);

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

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            var secondary = ThemeBrush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray);

            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = "Text Template Manager", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"Version {AppVersion()}", Foreground = secondary });
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
                XamlRoot = this.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ShowLicenseAsync();
        }

        private async Task ShowLicenseAsync()
        {
            if (this.XamlRoot == null) return;
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
            await new ContentDialog
            {
                Title = "License",
                Content = scroll,
                CloseButtonText = "Close",
                XamlRoot = this.XamlRoot
            }.ShowAsync();
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            if (this.XamlRoot == null) return;
            await new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            }.ShowAsync();
        }

        // ---- Auto-update ----

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await RunUpdateCheckAsync(manual: true);

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_readyInstallerPath != null && _readyVersionLabel != null)
                await PromptInstallAsync(_readyVersionLabel, _readyInstallerPath);
        }

        // Tag as shown to the user in the install prompt — drop a leading "v" so it reads "Version 0.9.6-beta".
        private static string DisplayTag(string tag) => string.IsNullOrWhiteSpace(tag) ? "" : tag.TrimStart('v', 'V');

        private async Task RunUpdateCheckAsync(bool manual)
        {
            // Enterprise policy (read-only registry) can hard-disable updates for everyone.
            if (!UpdatePolicy.UpdatesAllowed)
            {
                if (manual) await ShowMessageAsync("Check for Updates", "Updates are disabled by your organization.");
                return;
            }
            if (!manual && !DataNode.Instance.CurrentSettings.AutoCheckUpdates) return;
            if (_updateBusy) return;
            _updateBusy = true;
            try
            {
                // Beta is honored only when both the user setting and policy allow it.
                bool allowBeta = DataNode.Instance.CurrentSettings.AllowBetaUpdates && UpdatePolicy.BetaAllowed;
                UpdateService.UpdateInfo? info;
                try { info = await _updater.CheckAsync(allowBeta); }
                catch { if (manual) await ShowMessageAsync("Check for Updates", "Could not reach the update server."); return; }

                if (info == null)
                {
                    if (manual) await ShowMessageAsync("You're up to date", $"Version {AppVersion()} is the latest.");
                    return;
                }

                string? path;
                try { path = await _updater.EnsureDownloadedAsync(info); }
                catch { if (manual) await ShowMessageAsync("Update", "Found an update but couldn't download it."); return; }
                if (path == null) return;

                _readyInstallerPath = path;
                _readyVersionLabel = DisplayTag(info.Tag);
                UpdateButton.Visibility = Visibility.Visible;

                // Prompt on a manual check, or the first time a given version is seen (startup / newer release).
                // A deferred update just leaves the button visible; it re-prompts on the next launch.
                if (manual || _promptedVersion != info.Version)
                {
                    _promptedVersion = info.Version;
                    // Auto-detected update: the user may be in another app, so flash the taskbar.
                    if (!manual)
                        WindowHelper.FlashTaskbar(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                    await PromptInstallAsync(_readyVersionLabel, path);
                }
            }
            finally { _updateBusy = false; }
        }

        private async Task PromptInstallAsync(string versionLabel, string installerPath)
        {
            if (this.XamlRoot == null) return;   // too early; the Update-now button stays available
            var dialog = new ContentDialog
            {
                Title = "Update available",
                Content = $"Version {versionLabel} is ready to install. The app will close, update, and reopen.",
                PrimaryButtonText = "Install now",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await InstallUpdateAsync(installerPath);
        }

        private async Task InstallUpdateAsync(string installerPath)
        {
            // Persist first, then hand off to the silent installer and exit so files aren't locked.
            await FlushEditorAsync();
            _saveTimer?.Stop();
            await ViewModel.SaveCurrentStateAsync();

            if (UpdateService.LaunchInstaller(installerPath))
                (Application.Current as App)?.Shutdown();
            else
                await ShowMessageAsync("Update", "Could not start the installer.");
        }

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
