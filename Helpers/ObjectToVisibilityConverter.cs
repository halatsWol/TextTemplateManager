using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace TextTemplateManager.Helpers
{
    public class ObjectToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // If we have an "Invert" parameter, we want Visible if value is NULL
            bool invert = parameter?.ToString() == "Invert";

            // Check if the value exists (and specifically if it's a Template if needed)
            bool isVisible = value != null;

            if (invert) isVisible = !isVisible;

            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}