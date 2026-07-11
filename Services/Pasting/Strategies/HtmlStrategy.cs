using RtfPipe;
using System;
using System.Diagnostics;

namespace TextTemplateManager.Services.Pasting.Strategies
{
    public class HtmlStrategy : IPasteStrategy
    {
        public string ClipboardFormat => Windows.ApplicationModel.DataTransfer.StandardDataFormats.Html;

        public string Convert(string rtfContent)
        {
            if (string.IsNullOrWhiteSpace(rtfContent)) return string.Empty;

            try
            {
                // RtfPipe converts RTF to a valid HTML string
                string html = Rtf.ToHtml(rtfContent);

                // We return ONLY the HTML. 
                // WinRT's SetHtmlFormat will wrap this in the required 
                // Version:0.9 header and calculate offsets automatically.
                return html;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HTML STRATEGY] Error: {ex.Message}");
                return string.Empty;
            }
        }
    }
}