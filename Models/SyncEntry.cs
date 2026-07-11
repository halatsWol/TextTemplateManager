using CommunityToolkit.Mvvm.ComponentModel;

namespace TextTemplateManager.Models
{
    public partial class SyncEntry : ObservableObject
    {
        [ObservableProperty] private string _folderName = string.Empty;
        [ObservableProperty] private string _filePath = string.Empty;
        [ObservableProperty] private bool _isPaused;
        [ObservableProperty] private bool _allowSaveBack; // "checkbox to activate also save to this"
    }
}
