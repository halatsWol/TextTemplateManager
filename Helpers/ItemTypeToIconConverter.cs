using Microsoft.UI.Xaml.Data;
using System;
using TextTemplateManager.Common;

namespace TextTemplateManager.Helpers;

public class ItemTypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is ItemType type)
        {
            return type switch
            {
                ItemType.Folder => "\uE8B7",    // Standard Folder
                ItemType.SyncFolder => "\uE895",// Sync/Refresh icon (Symbol: Sync)
                ItemType.Template => "\uE8A5",  // Document icon
                _ => "\uE8B7"
            };
        }
        return "\uE8B7";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}