using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using TextTemplateManager.Data;
using TextTemplateManager.Models;
using Windows.Storage.Pickers;

namespace TextTemplateManager
{
    /// <summary>Lists the sync sources and persists changes to sync.ttmsettings.</summary>
    public sealed partial class SyncSettingsPage : Page
    {
        private SyncSettings _sync = new();
        private IntPtr _hwnd;
        private bool _loading;

        public SyncSettingsPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is long h) _hwnd = new IntPtr(h);

            _sync = DataNode.Instance.CurrentSyncSettings;

            // Only '-' and '.' are supported now; migrate any legacy value (e.g. '_') to '-'.
            if (_sync.Separator != "." && _sync.Separator != "-")
            {
                _sync.Separator = "-";
                _ = SaveAsync();
            }
            _loading = true;
            SeparatorBox.SelectedIndex = _sync.Separator == "." ? 1 : 0;
            _loading = false;

            SourceList.ItemsSource = _sync.Sources;
            foreach (var s in _sync.Sources) Subscribe(s);
            _sync.Sources.CollectionChanged += Sources_CollectionChanged;

            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _sync.Sources.CollectionChanged -= Sources_CollectionChanged;
            foreach (var s in _sync.Sources) Unsubscribe(s);
            _ = SaveAsync();
            base.OnNavigatedFrom(e);
        }

        private void SyncHelpButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => SyncHelpTip.IsOpen = true;

        // ---- Persistence ----
        private static System.Threading.Tasks.Task SaveAsync() => DataNode.Instance.SaveSyncSettingsAsync();

        private void Subscribe(SyncSource s) => s.PropertyChanged += Source_PropertyChanged;
        private void Unsubscribe(SyncSource s) => s.PropertyChanged -= Source_PropertyChanged;

        private async void Source_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SyncSource.IsFileMissing)) return; // computed, not persisted
            await SaveAsync();

            switch (e.PropertyName)
            {
                case nameof(SyncSource.IsActive):   // load/drop the folder
                case nameof(SyncSource.Path):       // re-link to a different file
                    await DataNode.Instance.ReapplySyncAsync();
                    break;
                case nameof(SyncSource.Name):       // just rename the folder live
                    if (sender is SyncSource s) UpdateFolderTitle(s);
                    break;
                    // AllowSave (write-through checks it live) and ShortcutPrefix need no reconcile.
            }
        }

        private static void UpdateFolderTitle(SyncSource s)
        {
            if (DataNode.Instance.LocalItems.FirstOrDefault(c => c.Id == s.Id) is BaseItem folder)
                folder.Title = s.Name;
        }

        private async void Sources_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (SyncSource s in e.OldItems) Unsubscribe(s);
            if (e.NewItems != null) foreach (SyncSource s in e.NewItems) Subscribe(s);
            await SaveAsync();
            if (e.Action != NotifyCollectionChangedAction.Move)
                await DataNode.Instance.ReapplySyncAsync();   // add / remove -> refresh tree (Move handled by buttons)
        }

        private async void Separator_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _sync.Separator = (SeparatorBox.SelectedItem as ComboBoxItem)?.Content as string ?? "-";
            await SaveAsync();
        }

        // Prefixes are always uppercase (matches how the multi-key buffer is shown). The x:Bind
        // carries the uppercased text into ShortcutPrefix.
        private void Prefix_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || sender is not TextBox tb) return;
            string upper = tb.Text.ToUpperInvariant();
            if (tb.Text == upper) return;
            int caret = tb.SelectionStart;
            tb.Text = upper;                                    // re-enters, but now equal -> no loop
            tb.SelectionStart = Math.Min(caret, tb.Text.Length);
        }

        // ---- Add / create ----
        private async void AddExisting_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add(".ttmdata");
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            _sync.Sources.Add(new SyncSource
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(file.Path),
                Path = file.Path,
                IsActive = true,
                AllowSave = false,
            });
        }

        private async void CreateNew_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeChoices.Add("TextTemplateManager data", new List<string> { ".ttmdata" });
            picker.SuggestedFileName = "Sync";

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            // Write an empty, valid sync file (a serialized root Folder).
            await StorageService.SaveAsync(file.Path, new Folder { Id = Guid.Empty, Title = "Root" });

            _sync.Sources.Add(new SyncSource
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(file.Path),
                Path = file.Path,
                IsActive = true,
                AllowSave = false,
            });
        }

        // ---- Per-row actions ----
        private static SyncSource Source(object sender) =>
            (sender as Microsoft.UI.Xaml.FrameworkElement)?.DataContext as SyncSource;

        private async void Relink_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (Source(sender) is not SyncSource s) return;

            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add(".ttmdata");
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            s.Path = file.Path; // raises IsFileMissing + persists via Source_PropertyChanged
        }

        private async void MoveUp_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (Source(sender) is not SyncSource s) return;
            int i = _sync.Sources.IndexOf(s);
            if (i > 0) { _sync.Sources.Move(i, i - 1); await ReapplyAsync(); }
        }

        private async void MoveDown_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (Source(sender) is not SyncSource s) return;
            int i = _sync.Sources.IndexOf(s);
            if (i >= 0 && i < _sync.Sources.Count - 1) { _sync.Sources.Move(i, i + 1); await ReapplyAsync(); }
        }

        private static async Task ReapplyAsync()
        {
            await SaveAsync();
            await DataNode.Instance.ReapplySyncAsync();
        }

        private void Remove_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (Source(sender) is SyncSource s) _sync.Sources.Remove(s);
        }
    }
}
