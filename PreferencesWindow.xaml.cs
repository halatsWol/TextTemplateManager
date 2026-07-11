using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Linq;
using TextTemplateManager.Models;
using TextTemplateManager.Data;
using Windows.Storage.Pickers;

namespace TextTemplateManager;

public sealed partial class PreferencesWindow : Window
{
    public AppSettings Settings { get; set; }

    public PreferencesWindow()
    {
        this.InitializeComponent();
        // Load settings from the singleton or service
        Settings = DataNode.Instance.CurrentSettings;

        // Default to General page
        SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var selectedItem = args.SelectedItem as NavigationViewItem;
        if (selectedItem?.Tag?.ToString() == "General")
        {
            ContentFrame.Navigate(typeof(GeneralSettingsPage), Settings);
        }
        else if (selectedItem?.Tag?.ToString() == "Sync")
        {
            // Pass this window's handle (as Int64 — IntPtr can't be marshalled through Navigate)
            // so the page's file pickers can anchor to it.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ContentFrame.Navigate(typeof(SyncSettingsPage), hwnd.ToInt64());
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await StorageService.SaveSettingsAsync(Settings);
        // Keep the OS autostart entry in sync with the persisted setting.
        Services.System.StartupManager.SetEnabled(Settings.RunAtStartup);
        this.Close();
    }
}