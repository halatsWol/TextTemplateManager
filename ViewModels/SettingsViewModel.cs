using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using TextTemplateManager.Data;
using TextTemplateManager.Models;
using Windows.Storage.Pickers;

namespace TextTemplateManager.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private AppSettings _settings;

    public SettingsViewModel(AppSettings currentSettings)
    {
        _settings = currentSettings;
    }

    [RelayCommand]
    private async Task AddSyncItem()
    {
        var picker = new FileOpenPicker();
        // Initialize picker with HWND...
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            Settings.SyncEntries.Add(new SyncEntry
            {
                FolderName = "New Sync Folder",
                FilePath = file.Path
            });
            await SaveSettings();
        }
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        await StorageService.SaveSettingsAsync(Settings);
        // Autostart via the HKCU Run key (see StartupManager).
        Services.System.StartupManager.SetEnabled(Settings.RunAtStartup);
    }
}