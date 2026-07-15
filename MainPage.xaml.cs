using MarflowSoftware.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
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

namespace TextTemplateManager
{
    public sealed partial class MainPage : Page
    {
        public MainViewModel ViewModel { get; }
        private bool _isInternalChange = false;
        private Timer _saveTimer;

        // References to the controls inside the DataTemplate
        private RichEditBox _richEditor;
        private Pivot _editorPivot;
        private bool _conflictUserMoved = false;   // user dragged the panel; stop auto-anchoring it
        private bool _conflictVisible = false;
        private Storyboard _conflictStoryboard;
        private string _dismissedNotesSignature;   // cross-area note set the user dismissed
        private string _currentNotesSignature;

        // Auto-update
        private readonly UpdateService _updater = new();
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateTimer;
        private string? _readyInstallerPath;
        private Version? _readyVersion;
        private string? _readyVersionLabel;  // release tag shown in the install prompt (e.g. "0.9.6-beta")
        private Version? _promptedVersion;   // last version we showed the modal prompt for
        private bool _updateBusy;



        public MainPage()
        {
            this.InitializeComponent();

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

        private void Template_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
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

        private Microsoft.UI.Dispatching.DispatcherQueueTimer _saveNotifTimer;
        private Storyboard _saveFade;

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

        #region Editor Lifecycle

        private void RichEditor_Loaded(object sender, RoutedEventArgs e)
        {
            _richEditor = sender as RichEditBox;
            SyncEditorToModel();
        }

        private void RichEditor_Unloaded(object sender, RoutedEventArgs e) => _richEditor = null;

        private void EditorPivot_Loaded(object sender, RoutedEventArgs e) => _editorPivot = sender as Pivot;

        #endregion

        #region Editor Sync

        private void SyncEditorToModel()
        {
            if (ViewModel?.SelectedItem is not Template t)
            {
                ClearEditors();
                return;
            }

            if (_richEditor?.Document == null) return;

            _isInternalChange = true;
            try
            {
                _saveTimer.Stop();
                string content = t.Content ?? string.Empty;
                content = content.TrimEnd('\0');

                if (content.StartsWith("{\\rtf1"))
                    _richEditor.Document.SetText(TextSetOptions.FormatRtf, content);
                else
                    _richEditor.Document.SetText(TextSetOptions.None, content);

                // Give paragraphs a little breathing room (space after each paragraph).
                ApplyDefaultParagraphSpacing();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Sync Error: {ex.Message}");
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() => _isInternalChange = false);
            }
        }

        private void RichEditor_TextChanged(object sender, RoutedEventArgs e)
        {
            if (_isInternalChange || _richEditor?.Document == null || ViewModel?.SelectedTemplate is not Template t) return;

            try
            {
                _richEditor.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
                if (t.Content != rtf)
                {
                    t.Content = rtf;
                    _saveTimer.Stop();
                    _saveTimer.Start();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Text Update Error: {ex.Message}");
            }
        }

        private void ClearEditors()
        {
            _isInternalChange = true;
            try { _richEditor?.Document?.SetText(TextSetOptions.None, string.Empty); }
            finally { _isInternalChange = false; }
        }

        private void EditorPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_editorPivot == null || _isInternalChange) return;

            if (ViewModel.SelectedTemplate is Template t && _editorPivot.SelectedIndex == 1)
            {
                _richEditor?.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            }
        }

        #endregion

        #region Formatting Buttons

        private void OnUndoClick(object sender, RoutedEventArgs e) => _richEditor?.Document.Undo();
        private void OnRedoClick(object sender, RoutedEventArgs e) => _richEditor?.Document.Redo();

        private void BoldButton_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor != null)
                _richEditor.Document.Selection.CharacterFormat.Bold = FormatEffect.Toggle;
        }

        private void ItalicButton_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor != null)
                _richEditor.Document.Selection.CharacterFormat.Italic = FormatEffect.Toggle;
        }

        private void UnderlineButton_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor != null)
                _richEditor.Document.Selection.CharacterFormat.Underline = UnderlineType.Single;
        }

        private void StrikeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor != null)
                _richEditor.Document.Selection.CharacterFormat.Strikethrough = FormatEffect.Toggle;
        }

        private void Heading_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor == null) return;
            if (sender is MenuFlyoutItem item && double.TryParse(item.Tag?.ToString(), out double size))
            {
                var format = _richEditor.Document.Selection.CharacterFormat;
                format.Size = (float)size;
                // Headings (anything above the Normal size) are bold; Normal is not.
                format.Bold = size > 12 ? FormatEffect.On : FormatEffect.Off;
                _richEditor.Document.Selection.CharacterFormat = format;
                PushEditorContentToModel();
            }
        }

        private void BulletList_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor == null) return;
            var format = _richEditor.Document.Selection.ParagraphFormat;
            format.ListType = format.ListType == MarkerType.Bullet ? MarkerType.None : MarkerType.Bullet;
            _richEditor.Document.Selection.ParagraphFormat = format;
            PushEditorContentToModel();
        }

        private void NumberList_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor == null) return;
            var format = _richEditor.Document.Selection.ParagraphFormat;
            if (format.ListType == MarkerType.Arabic)
            {
                format.ListType = MarkerType.None;
            }
            else
            {
                format.ListType = MarkerType.Arabic;
                format.ListStart = 1; // number from 1 instead of 0
            }
            _richEditor.Document.Selection.ParagraphFormat = format;
            PushEditorContentToModel();
        }

        private void ClearAllFormatting_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor == null || ViewModel.SelectedTemplate is not Template t) return;

            _richEditor.Document.GetText(TextGetOptions.None, out string plainText);
            _richEditor.Document.SetText(TextSetOptions.None, plainText);
            _richEditor.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            t.Content = rtf;
        }

        private void Preformat_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor == null) return;
            var format = _richEditor.Document.Selection.CharacterFormat;
            format.Name = "Consolas";
            format.Size = 13;
            format.Bold = FormatEffect.Off;
            format.Italic = FormatEffect.Off;
            _richEditor.Document.Selection.CharacterFormat = format;
            PushEditorContentToModel();
        }

        // ---- Color picker: visual swatch grid, tooltip = color name, only Auto is text ----

        // name + ARGB hex, laid out in a 5-column grid.
        private static readonly (string Name, string Hex)[] PaletteColors =
        {
            ("Black", "#FF000000"), ("Dark Gray", "#FF404040"), ("Gray", "#FF808080"), ("Light Gray", "#FFC8C8C8"), ("White", "#FFFFFFFF"),
            ("Dark Red", "#FF8B0000"), ("Red", "#FFE81123"), ("Orange", "#FFFF8C00"), ("Amber", "#FFFFB900"), ("Yellow", "#FFFFF100"),
            ("Dark Green", "#FF006400"), ("Green", "#FF107C10"), ("Teal", "#FF00B294"), ("Lime", "#FF00CC6A"), ("Cyan", "#FF00B7C3"),
            ("Dark Blue", "#FF00008B"), ("Blue", "#FF0078D7"), ("Light Blue", "#FF69B7EB"), ("Purple", "#FF881798"), ("Pink", "#FFE3008C"),
        };

        private void TextColorButton_Click(object sender, RoutedEventArgs e) => ShowColorPicker((FrameworkElement)sender, isHighlight: false);
        private void HighlightColorButton_Click(object sender, RoutedEventArgs e) => ShowColorPicker((FrameworkElement)sender, isHighlight: true);

        private void ShowColorPicker(FrameworkElement anchor, bool isHighlight)
        {
            if (_richEditor == null) return;

            var flyout = new Flyout();
            var root = new StackPanel { Spacing = 8, Padding = new Thickness(6) };

            // Auto / None is the only textual entry.
            var autoButton = new Button
            {
                Content = isHighlight ? "None" : "Automatic",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            autoButton.Click += (_, _) => { ApplyColor(isHighlight, "auto"); flyout.Hide(); };
            root.Children.Add(autoButton);

            const int cols = 5;
            var grid = new Grid();
            for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int rowCount = (PaletteColors.Length + cols - 1) / cols;
            for (int r = 0; r < rowCount; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var stroke = ThemeBrush("ControlStrongStrokeColorDefaultBrush", Microsoft.UI.Colors.Gray);
            for (int i = 0; i < PaletteColors.Length; i++)
            {
                var (name, hex) = PaletteColors[i];
                var swatch = new Border
                {
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(HexToColor(hex)),
                    BorderBrush = stroke,
                    BorderThickness = new Thickness(1)
                };
                ToolTipService.SetToolTip(swatch, name);

                // Slight hover emphasis.
                swatch.PointerEntered += (s, _) => ((Border)s).BorderThickness = new Thickness(2);
                swatch.PointerExited += (s, _) => ((Border)s).BorderThickness = new Thickness(1);

                string chosen = hex;
                swatch.Tapped += (_, _) => { ApplyColor(isHighlight, chosen); flyout.Hide(); };

                Grid.SetColumn(swatch, i % cols);
                Grid.SetRow(swatch, i / cols);
                grid.Children.Add(swatch);
            }

            root.Children.Add(grid);
            flyout.Content = root;
            flyout.ShowAt(anchor);
        }

        private void ApplyColor(bool isHighlight, string tag)
        {
            if (_richEditor == null) return;
            var format = _richEditor.Document.Selection.CharacterFormat;

            if (isHighlight)
                format.BackgroundColor = tag == "auto" ? Microsoft.UI.Colors.Transparent : HexToColor(tag);
            else if (tag == "auto" && _richEditor.Foreground is SolidColorBrush fg)
                format.ForegroundColor = fg.Color;
            else
                format.ForegroundColor = HexToColor(tag);

            _richEditor.Document.Selection.CharacterFormat = format;
            PushEditorContentToModel();
        }

        // ---- Table picker: visual 5x5 grid, hover highlights from the top-left ----

        private int _lastTableCols = 3;

        private void TableButton_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor == null) return;

            const int n = 5;
            var flyout = new Flyout();
            var root = new StackPanel { Spacing = 6, Padding = new Thickness(8), MinWidth = 170 };

            var label = new TextBlock { Text = "Insert table", HorizontalAlignment = HorizontalAlignment.Center };

            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < n; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var cells = new Border[n, n];
            var idle = ThemeBrush("ControlFillColorDefaultBrush", Microsoft.UI.Colors.WhiteSmoke);
            var stroke = ThemeBrush("ControlStrongStrokeColorDefaultBrush", Microsoft.UI.Colors.Gray);
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    var cell = new Border
                    {
                        Width = 22,
                        Height = 22,
                        Margin = new Thickness(2),
                        CornerRadius = new CornerRadius(3),
                        BorderBrush = stroke,
                        BorderThickness = new Thickness(1),
                        Background = idle
                    };
                    int rr = r, cc = c;
                    cell.PointerEntered += (_, _) =>
                    {
                        HighlightCells(cells, rr, cc);
                        label.Text = $"{rr + 1} × {cc + 1} table";
                    };
                    cell.Tapped += (_, _) => { InsertTable(rr + 1, cc + 1); flyout.Hide(); };
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    cells[r, c] = cell;
                    grid.Children.Add(cell);
                }
            }

            root.Children.Add(label);
            root.Children.Add(grid);
            root.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 4, 0, 4),
                Background = ThemeBrush("DividerStrokeColorDefaultBrush", Microsoft.UI.Colors.Gray)
            });

            var addRow = new Button { Content = "Insert row below caret", HorizontalAlignment = HorizontalAlignment.Stretch };
            addRow.Click += (_, _) => { InsertTableRowAtCaret(); flyout.Hide(); };
            root.Children.Add(addRow);

            var addCol = new Button { Content = "Insert column at caret", HorizontalAlignment = HorizontalAlignment.Stretch };
            addCol.Click += (_, _) => { InsertTableColumnAtCaret(); flyout.Hide(); };
            root.Children.Add(addCol);

            flyout.Content = root;
            flyout.ShowAt((FrameworkElement)sender);
        }

        private static void HighlightCells(Border[,] cells, int hoverRow, int hoverCol)
        {
            // Highlight the block from the top-left (0,0) to the hovered cell.
            var active = ThemeBrush("AccentFillColorDefaultBrush", Microsoft.UI.Colors.DodgerBlue);
            var idle = ThemeBrush("ControlFillColorDefaultBrush", Microsoft.UI.Colors.WhiteSmoke);
            for (int r = 0; r < cells.GetLength(0); r++)
                for (int c = 0; c < cells.GetLength(1); c++)
                    cells[r, c].Background = (r <= hoverRow && c <= hoverCol) ? active : idle;
        }

        private void InsertTable(int rows, int cols)
        {
            if (_richEditor == null) return;
            _lastTableCols = cols;
            try { _richEditor.Document.Selection.SetText(TextSetOptions.FormatRtf, BuildTableRtf(rows, cols)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Editor] Table insert failed: {ex.Message}"); }
            PushEditorContentToModel();
        }

        // Best-effort: inserts a fresh single row matching the last-inserted column count.
        private void InsertTableRowAtCaret()
        {
            if (_richEditor == null) return;
            try { _richEditor.Document.Selection.SetText(TextSetOptions.FormatRtf, BuildTableRtf(1, _lastTableCols)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Editor] Row insert failed: {ex.Message}"); }
            PushEditorContentToModel();
        }

        // Best-effort: inserts a fresh single-cell column snippet at the caret.
        private void InsertTableColumnAtCaret()
        {
            if (_richEditor == null) return;
            try { _richEditor.Document.Selection.SetText(TextSetOptions.FormatRtf, BuildTableRtf(1, 1)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Editor] Column insert failed: {ex.Message}"); }
            PushEditorContentToModel();
        }

        private static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key, Windows.UI.Color fallback)
            => Application.Current.Resources.TryGetValue(key, out var v) && v is Microsoft.UI.Xaml.Media.Brush b
                ? b
                : new SolidColorBrush(fallback);

        private static string BuildTableRtf(int rows, int cols)
        {
            const int cellTwips = 1800; // ~3.2 cm per column
            var sb = new System.Text.StringBuilder();
            sb.Append(@"{\rtf1\ansi\deff0 ");
            for (int r = 0; r < rows; r++)
            {
                sb.Append(@"\trowd\trgaph100");
                for (int c = 1; c <= cols; c++)
                    sb.Append(@"\cellx").Append(cellTwips * c);
                for (int c = 0; c < cols; c++)
                    sb.Append(@"\intbl \cell ");
                sb.Append(@"\row ");
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Push the editor RTF to the model (debounced) — for toolbar actions that don't
        /// raise TextChanged.</summary>
        private void PushEditorContentToModel()
        {
            if (_richEditor?.Document == null || ViewModel.SelectedTemplate is not Template t) return;

            _richEditor.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            if (t.Content != rtf)
            {
                t.Content = rtf;
                _saveTimer.Stop();
                _saveTimer.Start();
            }
        }

        private static Windows.UI.Color HexToColor(string hex)
        {
            hex = hex.TrimStart('#');
            byte a = 255;
            int offset = 0;
            if (hex.Length == 8) { a = Convert.ToByte(hex.Substring(0, 2), 16); offset = 2; }
            byte r = Convert.ToByte(hex.Substring(offset, 2), 16);
            byte g = Convert.ToByte(hex.Substring(offset + 2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(offset + 4, 2), 16);
            return Windows.UI.Color.FromArgb(a, r, g, b);
        }

        // ---- Editor keyboard behaviour: Tab indent / list level, Enter, Shift+Enter ----

        private const float ParagraphSpacing = 10f;   // points of space after each paragraph
        private const int MaxListLevel = 3;            // 3 nested sub-levels
        private const int TabSpaces = 4;               // a Tab inserts this many spaces in plain text
        private const float NormalSize = 11f;

        private void RichEditor_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_richEditor == null) return;

            bool shift = IsKeyDown(Windows.System.VirtualKey.Shift);

            // Tab / Shift+Tab: indent (or change list level) instead of moving focus.
            if (e.Key == Windows.System.VirtualKey.Tab)
            {
                e.Handled = true;
                AdjustListOrIndent(increase: !shift);
                return;
            }

            var sel = _richEditor.Document.Selection;

            // Shift+Enter: soft line break inside the current paragraph.
            if (e.Key == Windows.System.VirtualKey.Enter && shift)
            {
                e.Handled = true;
                sel.TypeText("\v");
                PushEditorContentToModel();
                return;
            }

            // Enter after a heading: start a fresh Normal paragraph (don't keep heading size).
            if (e.Key == Windows.System.VirtualKey.Enter && !shift)
            {
                var cf = sel.CharacterFormat;
                bool isHeading = cf.Size > NormalSize || cf.Bold == FormatEffect.On;
                if (isHeading)
                {
                    e.Handled = true;
                    sel.TypeText("\r"); // new paragraph

                    var nf = sel.CharacterFormat;
                    nf.Size = NormalSize;
                    nf.Bold = FormatEffect.Off;
                    sel.CharacterFormat = nf;

                    var pf = sel.ParagraphFormat;
                    pf.SpaceAfter = ParagraphSpacing;
                    sel.ParagraphFormat = pf;

                    PushEditorContentToModel();
                }
            }
        }

        private void AdjustListOrIndent(bool increase)
        {
            if (_richEditor == null) return;
            var sel = _richEditor.Document.Selection;
            var pf = sel.ParagraphFormat;

            if (pf.ListType is not MarkerType.None and not MarkerType.Undefined)
            {
                // List item: change nesting level (0..MaxListLevel).
                int level = Math.Clamp(pf.ListLevelIndex + (increase ? 1 : -1), 0, MaxListLevel);
                pf.ListLevelIndex = level;
                sel.ParagraphFormat = pf;
            }
            else if (increase)
            {
                // Plain text: insert a tab as 4 spaces.
                sel.TypeText(new string(' ', TabSpaces));
            }
            else
            {
                // Shift+Tab: remove up to 4 spaces immediately before the caret.
                RemovePrecedingSpaces(TabSpaces);
            }

            PushEditorContentToModel();
        }

        private void RemovePrecedingSpaces(int max)
        {
            if (_richEditor == null) return;
            var sel = _richEditor.Document.Selection;
            if (sel.Length != 0) return; // only act on a plain caret

            int caret = sel.EndPosition;
            int probeStart = Math.Max(0, caret - max);
            var probe = _richEditor.Document.GetRange(probeStart, caret);
            probe.GetText(TextGetOptions.None, out string before);

            int remove = 0;
            for (int i = before.Length - 1; i >= 0 && before[i] == ' ' && remove < max; i--)
                remove++;

            if (remove > 0)
            {
                var del = _richEditor.Document.GetRange(caret - remove, caret);
                del.SetText(TextSetOptions.None, string.Empty);
            }
        }

        private void ApplyDefaultParagraphSpacing()
        {
            if (_richEditor?.Document == null) return;
            _richEditor.Document.GetText(TextGetOptions.None, out string all);
            if (string.IsNullOrEmpty(all)) return;

            var range = _richEditor.Document.GetRange(0, all.Length);
            var pf = range.ParagraphFormat;
            pf.SpaceAfter = ParagraphSpacing;
            range.ParagraphFormat = pf;
        }

        private static bool IsKeyDown(Windows.System.VirtualKey key)
            => Microsoft.UI.Input.InputKeyboardSource
                   .GetKeyStateForCurrentThread(key)
                   .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        #endregion

        #region Menu Handlers

        private void MainHelpButton_Click(object sender, RoutedEventArgs e) => MainHelpTip.IsOpen = true;

        private void AddTemplate_Click(object sender, RoutedEventArgs e) => ViewModel.AddTemplateCommand.Execute(null);
        private void AddFolder_Click(object sender, RoutedEventArgs e) => ViewModel.AddFolderCommand.Execute(null);
        private void CloneItem_Click(object sender, RoutedEventArgs e) => ViewModel.CloneItemCommand.Execute(null);
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
        }

        private const string GitHubUrl = "https://github.com/halatsWol/TextTemplateManager";
        private const string ContactEmail = "contact@kmarflow.com";

        private void GoToGitHub_Click(object sender, RoutedEventArgs e) => OpenExternal(GitHubUrl);

        private async void OpenHandbook_Click(object sender, RoutedEventArgs e)
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Handbook.pdf");
            if (System.IO.File.Exists(path)) OpenExternal(path);
            else await ShowMessageAsync("Handbook unavailable",
                "The handbook PDF was not found next to the app. It is generated at build time.");
        }

        // Opens a URL or local file with the shell's default handler.
        private static void OpenExternal(string target)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Help] open failed: {ex.Message}"); }
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
                _readyVersion = info.Version;
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

        #region Clipboard

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor == null || ViewModel.SelectedItem is not Template t) return;

            var package = new DataPackage();
            _richEditor.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            _richEditor.Document.GetText(TextGetOptions.None, out string plain);

            package.SetRtf(VariableHelper.ProcessVariables(rtf));
            package.SetText(VariableHelper.ProcessVariables(plain.TrimEnd('\r', '\n')));
            Clipboard.SetContent(package);
        }

        private void CopyPlain_Click(object sender, RoutedEventArgs e)
        {
            if (_richEditor == null || ViewModel.SelectedItem is not Template t) return;

            _richEditor.Document.GetText(TextGetOptions.None, out string plain);
            var package = new DataPackage();
            package.SetText(VariableHelper.ProcessVariables(plain.TrimEnd('\r', '\n')));
            Clipboard.SetContent(package);
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

        private static BaseItem FindItemFromSource(object source)
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

        private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
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

            // Cross-area duplicates: informational note, not a blocking conflict.
            var crossAreaNotes = allTemplates
                .Where(t => t.HasSingleKeyCrossAreaWarning)
                .GroupBy(t => t.SingleKeyShortcut)
                .ToList();

            bool hasErrors = singleConflicts.Any() || multiConflicts.Any();
            bool hasNotes = crossAreaNotes.Any();

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
                crossAreaNotes.SelectMany(g => g.Select(t => g.Key + "~" + t.Title))
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

            // Informational (non-blocking) notes for the same single key used across areas.
            if (crossAreaNotes.Any())
            {
                ConflictStack.Children.Add(new TextBlock
                {
                    Text = "Note — same single key in different areas (allowed; local wins, then sync order):",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray),
                    Margin = new Thickness(0, 8, 0, 4)
                });

                foreach (var g in crossAreaNotes)
                    foreach (var t in g)
                        ConflictStack.Children.Add(new TextBlock
                        {
                            Text = $"- {g.Key}: {t.Title}",
                            Foreground = ThemeBrush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray),
                            Margin = new Thickness(20, 0, 0, 0)
                        });
            }
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
