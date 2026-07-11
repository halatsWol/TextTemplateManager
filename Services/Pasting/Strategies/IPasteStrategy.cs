namespace TextTemplateManager.Services.Pasting.Strategies;

public interface IPasteStrategy
{
    // The service provides raw RTF, the strategy returns the processed string
    string Convert(string rtfContent);

    // This tells the Service WHICH clipboard format to use (Text, Rtf, etc)
    string ClipboardFormat { get; }
}