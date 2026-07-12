namespace TextTemplateManager.Common
{
    public enum ItemType
    {
        Folder,
        Template,
        SyncFolder
    }

    public enum PasteMode
    {
        Plaintext,
        Markdown,
        RTF,
        HTML,
        Auto,
        // HTML tuned for Atlassian Jira: preserves callout panels (data-panel-type divs) so
        // Jira's comment editor reconstructs them. Displayed as "HTML/Jira" (see PasteModeLabel).
        Jira
    }


}