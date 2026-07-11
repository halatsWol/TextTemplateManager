using System;

namespace TextTemplateManager.Helpers
{
    public class ObjectToBoolConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value != null; // Returns true if we have a selection
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
}
