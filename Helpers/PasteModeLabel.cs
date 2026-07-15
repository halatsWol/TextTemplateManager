using Microsoft.UI.Xaml.Data;
using System;
using TextTemplateManager.Common;

namespace TextTemplateManager.Helpers
{
    /// <summary>Display names for <see cref="PasteMode"/>. Most modes use their enum name; a few
    /// have a friendlier label (e.g. <see cref="PasteMode.Jira"/> shows as "HTML/Jira").</summary>
    public static class PasteModeLabel
    {
        /// <summary>The order paste modes are shown in menus and drop-downs (most-used first).
        /// The enum order is fixed for on-disk compatibility, so display order lives here.</summary>
        public static readonly PasteMode[] DisplayOrder =
        {
            PasteMode.Auto, PasteMode.Jira, PasteMode.HTML, PasteMode.RTF, PasteMode.Markdown, PasteMode.Plaintext,
        };

        public static string For(PasteMode mode) => mode switch
        {
            PasteMode.Jira => "HTML/Jira",
            _ => mode.ToString(),
        };
    }

    /// <summary>Binds a <see cref="PasteMode"/> to its display label (see <see cref="PasteModeLabel"/>).</summary>
    public class PasteModeLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is PasteMode mode ? PasteModeLabel.For(mode) : value?.ToString() ?? string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
