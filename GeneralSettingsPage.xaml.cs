using System;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using TextTemplateManager.Helpers;
using TextTemplateManager.Models;
using Windows.System;
using Windows.UI.Core;
using UpdatePolicy = TextTemplateManager.Services.System.UpdatePolicy;
using UpdatePolicyLevel = TextTemplateManager.Services.System.UpdatePolicyLevel;

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
        HotkeyTextBox.Text = FormatHotkey(ViewModel?.PasteWindowHotkey);
        InitUpdatePolicyUi();
        RefreshConnectorUi();
        ApiDocLink.NavigateUri = new Uri(ApiDocUrl());   // point at the doc for the installed version
        base.OnNavigatedTo(e);
    }

    // Links to the API doc at the tag matching the installed version (v<version>), so it always
    // matches the connector the user is running; dev/local builds fall back to the main branch.
    private static string ApiDocUrl()
    {
        const string blob = "https://github.com/halatsWol/TextTemplateManager/blob";
        const string path = "docs/BrowserConnectorApi.md";
        string v = InstalledVersion();
        string reference = string.IsNullOrEmpty(v) || v.StartsWith("0.0.0") || v.Contains("dev", StringComparison.OrdinalIgnoreCase)
            ? "main" : "v" + v;
        return $"{blob}/{reference}/{path}";
    }

    private static string InstalledVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        var ver = asm.GetName().Version ?? new Version(0, 0, 0);
        return $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    // ---- Browser connector ----
    private bool _connectorLoading;

    private void RefreshConnectorUi()
    {
        if (ViewModel == null) return;
        _connectorLoading = true;
        ConnectorToggle.IsOn = ViewModel.BrowserConnectorEnabled;
        ConnectorPortBox.Text = ViewModel.BrowserConnectorPort.ToString();
        ConnectorTokenBox.Text = ViewModel.BrowserConnectorToken;
        ConnectorDetails.Visibility = ViewModel.BrowserConnectorEnabled ? Visibility.Visible : Visibility.Collapsed;
        _connectorLoading = false;
    }

    private void Connector_Toggled(object sender, RoutedEventArgs e)
    {
        if (_connectorLoading || ViewModel == null) return;
        ViewModel.BrowserConnectorEnabled = ConnectorToggle.IsOn;
        _ = TextTemplateManager.Data.StorageService.SaveSettingsAsync(ViewModel);
        (Application.Current as App)?.ApplyBrowserConnectorSettings();   // start/stop; generates the token on first enable
        RefreshConnectorUi();                                           // reflect the generated token + visibility
    }

    private void ConnectorPort_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_connectorLoading || ViewModel == null) return;
        if (int.TryParse(ConnectorPortBox.Text, out int port) && port is > 1024 and < 65536)
        {
            if (port != ViewModel.BrowserConnectorPort)
            {
                ViewModel.BrowserConnectorPort = port;
                _ = TextTemplateManager.Data.StorageService.SaveSettingsAsync(ViewModel);
                if (ViewModel.BrowserConnectorEnabled) (Application.Current as App)?.ApplyBrowserConnectorSettings();
            }
        }
        else ConnectorPortBox.Text = ViewModel.BrowserConnectorPort.ToString();   // revert an invalid entry
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        var dp = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(ViewModel?.BrowserConnectorToken ?? "");
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    private void RegenerateToken_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        ViewModel.BrowserConnectorToken = Guid.NewGuid().ToString("N");
        _ = TextTemplateManager.Data.StorageService.SaveSettingsAsync(ViewModel);
        if (ViewModel.BrowserConnectorEnabled) (Application.Current as App)?.ApplyBrowserConnectorSettings();
        RefreshConnectorUi();
    }

    // Grays out the update toggles per enterprise policy without mutating the stored settings
    // (the check itself is also gated in RunUpdateCheckAsync).
    private bool _updatePolicyLoading;

    private void InitUpdatePolicyUi()
    {
        if (ViewModel == null) return;
        _updatePolicyLoading = true;

        var policy = UpdatePolicy.Current;
        bool updatesAllowed = policy != UpdatePolicyLevel.NoUpdate;
        bool betaAllowed = policy == UpdatePolicyLevel.AllowAll;

        AutoUpdateToggle.IsOn = updatesAllowed && ViewModel.AutoCheckUpdates;
        AutoUpdateToggle.IsEnabled = updatesAllowed;

        BetaToggle.IsOn = betaAllowed && ViewModel.AllowBetaUpdates;
        BetaToggle.IsEnabled = betaAllowed && AutoUpdateToggle.IsOn;

        UpdatePolicyNote.Text = policy switch
        {
            UpdatePolicyLevel.NoUpdate => "Updates are disabled by your organization.",
            UpdatePolicyLevel.NoBeta => "Beta updates are disabled by your organization.",
            _ => "",
        };
        UpdatePolicyNote.Visibility = policy == UpdatePolicyLevel.AllowAll ? Visibility.Collapsed : Visibility.Visible;

        _updatePolicyLoading = false;
    }

    private void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatePolicyLoading || ViewModel == null) return;
        ViewModel.AutoCheckUpdates = AutoUpdateToggle.IsOn;
        // Beta only matters while auto-update is on (and policy permits it).
        BetaToggle.IsEnabled = UpdatePolicy.BetaAllowed && AutoUpdateToggle.IsOn;
        _ = TextTemplateManager.Data.StorageService.SaveSettingsAsync(ViewModel);
    }

    private void Beta_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatePolicyLoading || ViewModel == null) return;
        ViewModel.AllowBetaUpdates = BetaToggle.IsOn;
        _ = TextTemplateManager.Data.StorageService.SaveSettingsAsync(ViewModel);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ApplyHotkey();   // safety: re-register the hotkey if the field never lost focus
        base.OnNavigatedFrom(e);
    }

    // "None"/empty means no hotkey — show a blank field (the placeholder shows through).
    private static string FormatHotkey(string? hotkey)
        => string.IsNullOrEmpty(hotkey) || hotkey == "None" ? "" : hotkey;

    private void RunAtStartup_Toggled(object sender, RoutedEventArgs e)
    {
        // Apply + persist immediately (also fires once on load — idempotent).
        if (ViewModel == null || sender is not ToggleSwitch ts) return;
        ViewModel.RunAtStartup = ts.IsOn;
        TextTemplateManager.Services.System.StartupManager.SetEnabled(ts.IsOn);
        _ = TextTemplateManager.Data.StorageService.SaveSettingsAsync(ViewModel);
    }

    // True once a full modifier(s)+key combo is committed; reset when a new bare modifier starts one.
    private bool _hotkeyCommitted = true;

    private void HotkeyTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;

        var mods = HeldModifiers();

        if (IsModifierKey(e.Key))
        {
            // Live feedback: show the modifiers held so far (not yet a valid shortcut).
            HotkeyTextBox.Text = string.Join("+", mods);   // "Shift", "Shift+Alt", …
            _hotkeyCommitted = false;
            return;
        }

        if (mods.Count == 0) return;   // a bare key is not a valid global shortcut — ignore

        mods.Add(e.Key.ToString());
        string hotkey = string.Join("+", mods);
        HotkeyTextBox.Text = hotkey;
        CommitHotkey(hotkey);
        _hotkeyCommitted = true;
    }

    private void HotkeyTextBox_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        // Every key released without completing a valid shortcut → clear the field (and the hotkey).
        if (!_hotkeyCommitted && HeldModifiers().Count == 0)
        {
            HotkeyTextBox.Text = "";
            CommitHotkey("None");
        }
    }

    // Modifiers currently held, in a stable display order (Shift+Alt matches the default).
    private System.Collections.Generic.List<string> HeldModifiers()
    {
        var mods = new System.Collections.Generic.List<string>();
        if (IsKeyDown(VirtualKey.Control)) mods.Add("Ctrl");
        if (IsKeyDown(VirtualKey.Shift)) mods.Add("Shift");
        if (IsKeyDown(VirtualKey.Menu)) mods.Add("Alt");   // Alt = 'Menu' in Win32/WinUI
        return mods;
    }

    // Only sets the pending value; OS registration waits for focus loss, so the combo you are
    // pressing can't fire itself (opening Quick Paste and swallowing the final key).
    private void CommitHotkey(string hotkey) => ViewModel.PasteWindowHotkey = hotkey;

    private void HotkeyTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Suspend the global hotkey while capturing.
        if (Application.Current is App app) app.UpdateGlobalHotkey("None");
    }

    private void HotkeyTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyHotkey();

    private void ApplyHotkey()
    {
        if (ViewModel == null) return;
        if (Application.Current is App app) app.UpdateGlobalHotkey(ViewModel.PasteWindowHotkey);
        _ = TextTemplateManager.Data.StorageService.SaveSettingsAsync(ViewModel);
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
        HotkeyTextBox.Text = "";
        ViewModel.PasteWindowHotkey = "None";
        ApplyHotkey();
    }
}