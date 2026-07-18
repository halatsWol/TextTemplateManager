using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TextTemplateManager.Models;

namespace TextTemplateManager.Helpers
{
    public class ItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate TemplateTemplate { get; set; } = null!;   // set in XAML
        public DataTemplate FolderTemplate { get; set; } = null!;     // set in XAML
        public DataTemplate DefaultTemplate { get; set; } = null!;    // set in XAML

        protected override DataTemplate SelectTemplateCore(object item)
        {
            return Select(item);
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            return Select(item);
        }

        private DataTemplate Select(object item)
        {
            if (item is Template) return TemplateTemplate;
            if (item is Folder) return FolderTemplate;
            return DefaultTemplate;
        }
    }
}
