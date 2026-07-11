using TextTemplateManager.Services.Pasting.Strategies;

public class RichTextStrategy : IPasteStrategy
{
    public string Convert(string rtfContent)
    {
        System.Diagnostics.Debug.WriteLine($"[TRACE] Strategy: RTF - Input Length: {rtfContent?.Length ?? 0}");
        return rtfContent;
    }
    public string ClipboardFormat => Windows.ApplicationModel.DataTransfer.StandardDataFormats.Rtf;
}