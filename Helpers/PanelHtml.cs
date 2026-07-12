using System;
using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;

namespace TextTemplateManager.Helpers
{
    /// <summary>
    /// Turns the editor's callout panels (<c>div[data-panel-type]</c>) into the right form for
    /// each paste mode. In the editor the panel's background and icon are CSS-only, so the stored
    /// HTML is just a bare div — every paste target would otherwise render it as plain text.
    ///
    /// Two cross-cutting concerns:
    ///  • Leading-panel unwrap. ProseMirror-based editors (Jira) parse external HTML with
    ///    <c>Slice.maxOpen</c>, which opens a leading block and drops a panel that is the first
    ///    node. Wrapping in <c>data-pm-slice="0 0 []"</c> forces the receiver to keep every
    ///    top-level block closed. Non-ProseMirror targets ignore it.
    ///  • Visibility. For non-Jira targets the panel needs real inline styling / a table / a
    ///    blockquote to look like a callout.
    /// </summary>
    public static class PanelHtml
    {
        private static readonly HashSet<string> PanelTypes =
            new(StringComparer.OrdinalIgnoreCase) { "info", "note", "success", "warning", "error" };

        /// <summary>Background, accent (left rail / border), leading emoji, and label for a panel
        /// type. Shared by the HTML, RTF, and Markdown converters so every mode looks consistent.</summary>
        public static (string Bg, string Accent, string Emoji, string Label) StyleFor(string? type) =>
            (type ?? "").ToLowerInvariant() switch
            {
                "note" => ("#eae6ff", "#6554c0", "🗒️", "Note"),
                "success" => ("#e3fcef", "#36b37e", "✅", "Success"),
                "warning" => ("#fffae6", "#ffab00", "⚠️", "Warning"),
                "error" => ("#ffebe6", "#ff5630", "⛔", "Error"),
                _ => ("#deebff", "#2684ff", "ℹ️", "Info"),
            };

        private static bool HasPanel(string? html) =>
            !string.IsNullOrEmpty(html) && html!.Contains("data-panel-type", StringComparison.OrdinalIgnoreCase);

        private static string ClosedSlice(string inner) => "<div data-pm-slice=\"0 0 []\">" + inner + "</div>";

        /// <summary>Wraps panel-containing HTML so ProseMirror keeps every top-level block closed
        /// (defeats the leading-panel unwrap). Non-panel HTML is returned unchanged.</summary>
        public static string WrapClosedSlice(string? html) =>
            HasPanel(html) ? ClosedSlice(html!) : html ?? string.Empty;

        /// <summary>HTML/Jira mode: normalize panels to Jira's structure, then wrap as a closed
        /// slice so Atlassian's editor rebuilds them as native panels.</summary>
        public static string Prepare(string? html)
        {
            if (!HasPanel(html)) return html ?? string.Empty;

            var doc = Load(html!);
            foreach (var panel in Panels(doc))
            {
                string type = NormalizeType(panel);
                panel.SetAttributeValue("class", "ak-editor-panel"); // drop the app-only ttm-panel class

                // Jira's panel node holds block content; wrap bare inline content in a paragraph.
                if (!panel.ChildNodes.Any(n => n.NodeType == HtmlNodeType.Element))
                    panel.InnerHtml = "<p>" + panel.InnerHtml + "</p>";
            }
            return ClosedSlice(doc.DocumentNode.OuterHtml);
        }

        /// <summary>HTML and Auto modes: replace each panel with a single-cell, inline-styled table
        /// (tinted background + left accent rail + inline icon). A table cell keeps the background
        /// filling reliably in Outlook desktop, where a plain styled div does not. Drops
        /// <c>data-panel-type</c> — reconstructing native Jira panels is the job of the HTML/Jira mode.</summary>
        public static string ToStyledHtml(string? html)
        {
            if (!HasPanel(html)) return html ?? string.Empty;

            var doc = Load(html!);
            bool converted = false;
            foreach (var panel in Panels(doc))
            {
                var (bg, accent, emoji, _) = StyleFor(panel.GetAttributeValue("data-panel-type", "info"));

                // Put the icon inline at the start of the first line (the editor draws it via CSS).
                var firstP = panel.SelectSingleNode(".//p");
                if (firstP != null) firstP.InnerHtml = emoji + "&nbsp;" + firstP.InnerHtml;
                else panel.InnerHtml = emoji + "&nbsp;" + panel.InnerHtml;

                string table =
                    "<table cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;margin:8px 0;\"><tr>" +
                    $"<td style=\"background-color:{bg};border-left:4px solid {accent};padding:8px 12px;\">{panel.InnerHtml}</td>" +
                    "</tr></table>";
                panel.ParentNode.ReplaceChild(HtmlNode.CreateNode(table), panel);
                converted = true;
            }
            string outHtml = doc.DocumentNode.OuterHtml;
            return converted ? ClosedSlice(outHtml) : outHtml;
        }

        // ---- helpers ----

        private static HtmlDocument Load(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return doc;
        }

        private static IEnumerable<HtmlNode> Panels(HtmlDocument doc) =>
            doc.DocumentNode.SelectNodes("//div[@data-panel-type]") ?? Enumerable.Empty<HtmlNode>();

        private static string NormalizeType(HtmlNode panel)
        {
            string type = panel.GetAttributeValue("data-panel-type", "info").ToLowerInvariant();
            if (!PanelTypes.Contains(type)) type = "info";
            panel.SetAttributeValue("data-panel-type", type);
            return type;
        }
    }
}
