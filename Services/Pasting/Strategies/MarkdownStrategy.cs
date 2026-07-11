using TextTemplateManager.Services.Pasting.Strategies;

public class MarkdownStrategy : IPasteStrategy
{
    public string Convert(string rtfContent) => new PlainTextStrategy().Convert(rtfContent);
    public string ClipboardFormat => Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text;
}