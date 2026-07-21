using HtmlAgilityPack;
using System;
using System.Linq;

namespace TextTemplateManager.Helpers
{
    /// <summary>
    /// Strips active/script content from untrusted HTML. Templates created through the browser
    /// connector carry a page selection, which can contain attacker-controlled markup; storing it
    /// verbatim would let it ride the clipboard (or the connector's own responses) into other apps.
    /// This removes script-bearing elements, event-handler attributes, and dangerous URL schemes.
    /// (The in-app editor sanitizes on display too; this keeps the *stored* content clean as well.)
    /// </summary>
    public static class HtmlSanitizer
    {
        // Elements dropped entirely (with their contents) — they run script or load remote content.
        private static readonly string[] DropTags =
        {
            "script", "style", "iframe", "object", "embed", "link", "meta", "base",
            "form", "applet", "frame", "frameset", "noscript", "template",
        };

        // Attributes that carry a URL; a javascript:/vbscript:/data: value in one is executable.
        private static readonly string[] UrlAttrs =
        {
            "href", "src", "action", "formaction", "background", "poster", "xlink:href", "data",
        };

        public static string Sanitize(string? html)
        {
            if (string.IsNullOrEmpty(html)) return html ?? "";

            HtmlDocument doc;
            try { doc = new HtmlDocument(); doc.LoadHtml(html); }
            catch { return ""; }   // unparseable -> store nothing rather than raw markup

            foreach (var node in doc.DocumentNode.Descendants()
                         .Where(n => n.NodeType == HtmlNodeType.Element &&
                                     DropTags.Contains(n.Name.ToLowerInvariant()))
                         .ToList())
                node.Remove();

            foreach (var el in doc.DocumentNode.Descendants()
                         .Where(n => n.NodeType == HtmlNodeType.Element).ToList())
            {
                foreach (var attr in el.Attributes.ToList())
                {
                    string name = attr.Name.ToLowerInvariant();
                    if (name.StartsWith("on", StringComparison.Ordinal))        // onclick, onerror, onload, ...
                        attr.Remove();
                    else if (UrlAttrs.Contains(name) && IsDangerousUrl(attr.Value))
                        attr.Remove();
                    else if (name == "style" &&
                             attr.Value.Contains("expression", StringComparison.OrdinalIgnoreCase))
                        attr.Remove();                                          // legacy IE CSS expression()
                }
            }

            return doc.DocumentNode.OuterHtml;
        }

        private static bool IsDangerousUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            // Decode entities and drop whitespace/NULs that could hide the scheme (e.g. "java\0script:").
            string v = System.Net.WebUtility.HtmlDecode(value);
            v = new string(v.Where(c => !char.IsWhiteSpace(c) && c != '\0').ToArray());
            return v.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        }
    }
}
