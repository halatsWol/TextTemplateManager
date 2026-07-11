using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace TextTemplateManager.Helpers
{
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool b = value is bool v && v;
            // ConverterParameter="Invert" flips the result, so one bool can drive a pair of
            // mutually-exclusive elements without a second property.
            if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase)) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value is Visibility v && v == Visibility.Visible;
    }
}
