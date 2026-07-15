using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using System.Linq;
using TextTemplateManager.Common;

namespace TextTemplateManager.Helpers;

public class EnumToCollectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // We ignore the 'value' and just return the PasteMode enum values
        // This ensures the ComboBox ALWAYS has its list, regardless of the template state.
        return Enum.GetValues(typeof(PasteMode)).Cast<PasteMode>();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        // CRITICAL: If the UI unloads and tries to set the value to null,
        // we return UnsetValue to tell the binding "Don't touch the model!"
        if (value == null) return DependencyProperty.UnsetValue;

        return value;
    }

}