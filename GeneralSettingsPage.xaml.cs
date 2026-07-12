using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using TextTemplateManager.Common;
using TextTemplateManager.Helpers;
using TextTemplateManager.Models;
using Windows.System;
using Windows.UI.Core;

namespace TextTemplateManager;

public sealed partial class GeneralSettingsPage : Page
{
    public AppSettings ViewModel { get; set; }

    public GeneralSettingsPage()
    {
        this.InitializeComponent();
        PasteModeCombo.ItemsSource = PasteModeLabel.DisplayOrder;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is AppSettings settings)
        {
            ViewModel = settings;
        }
        base.OnNavigatedTo(e);
    }

    private void RunAtStartup_Toggled(object sender, RoutedEventArgs e)
    {
        // Apply + persist immediately (also fires once on load — idempotent).
        if (ViewModel == null || sender is not ToggleSwitch ts) return;
        ViewModel.RunAtStartup = ts.IsOn;
        TextTemplateManager.Services.System.StartupManager.SetEnabled(ts.IsOn);
        _ = TextTemplateManager.Data.StorageService.SaveSettingsAsync(ViewModel);
    }

    private void HotkeyTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        var key = e.Key;

        if (IsModifierKey(key)) return;   // wait for a non-modifier

        bool ctrl = IsKeyDown(VirtualKey.Control);
        bool alt = IsKeyDown(VirtualKey.Menu);   // Alt = 'Menu' in Win32/WinUI
        bool shift = IsKeyDown(VirtualKey.Shift);

        string newHotkey = "";
        if (ctrl) newHotkey += "Ctrl+";
        if (alt) newHotkey += "Alt+";
        if (shift) newHotkey += "Shift+";
        newHotkey += key.ToString();

        ViewModel.PasteWindowHotkey = newHotkey;
        if (Application.Current is App myApp)
            myApp.UpdateGlobalHotkey(newHotkey);
    }

    private bool IsModifierKey(VirtualKey key)
    {
        return key == VirtualKey.Control || key == VirtualKey.Menu ||
               key == VirtualKey.Shift || key == VirtualKey.LeftWindows ||
               key == VirtualKey.RightWindows;
    }

    private bool IsKeyDown(VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.PasteWindowHotkey = "None";
        if (Application.Current is App myApp) myApp.UpdateGlobalHotkey("None");
    }
}