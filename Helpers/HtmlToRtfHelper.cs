using System.Text;
using HtmlAgilityPack;

namespace TextTemplateManager.Helpers
{
    /// <summary>
    /// Converts the editor's HTML into RTF (for the RTF paste mode and as part of Auto).
    /// Handles bold/italic/underline/strike, paragraphs, line breaks, headings, and
    /// bullet/numbered lists. It is intentionally lightweight — for full fidelity most
    /// targets accept the HTML clipboard format.
    /// </summary>
    public static class HtmlConverter
    {
        public static string ConvertHtmlToRtf(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            var rtf = new StringBuilder();
            rtf.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}");
            rtf.Append(@"\f0\fs22 ");

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            foreach (var node in doc.DocumentNode.ChildNodes)
                ProcessNode(node, rtf);

            rtf.Append('}');
            return rtf.ToString();
        }

        private static void ProcessNode(HtmlNode node, StringBuilder rtf)
        {
            string name = node.Name.ToLowerInvariant();

            // Opening tags.
            switch (name)
            {
                case "b": case "strong": rtf.Append(@"\b "); break;
                case "i": case "em": rtf.Append(@"\i "); break;
                case "u": rtf.Append(@"\ul "); break;
                case "s": case "strike": case "del": rtf.Append(@"\strike "); break;
                case "br": rtf.Append(@"\line "); break;
                case "p": case "div": rtf.Append(@"\par "); break;
                case "h1": rtf.Append(@"\par \b\fs36 "); break;
                case "h2": rtf.Append(@"\par \b\fs32 "); break;
                case "h3": rtf.Append(@"\par \b\fs28 "); break;
                case "h4": rtf.Append(@"\par \b\fs26 "); break;
                case "h5": rtf.Append(@"\par \b\fs24 "); break;
                case "h6": rtf.Append(@"\par \b\fs23 "); break;
                case "li":
                    rtf.Append(@"\par ");
                    if (node.ParentNode?.Name?.ToLowerInvariant() == "ol")
                        rtf.Append(ListIndex(node)).Append(@".\tab ");
                    else
                        rtf.Append(@"\bullet\tab ");
                    break;
            }

            // Content / children.
            if (node.HasChildNodes)
            {
                foreach (var child in node.ChildNodes)
                    ProcessNode(child, rtf);
            }
            else if (node.NodeType == HtmlNodeType.Text)
            {
                // Escape the RTF backslash FIRST, then braces (order matters).
                string text = System.Net.WebUtility.HtmlDecode(node.InnerText)
                    .Replace(@"\", @"\\")
                    .Replace("{", @"\{")
                    .Replace("}", @"\}");
                rtf.Append(text);
            }

            // Closing tags.
            switch (name)
            {
                case "b": case "strong": rtf.Append(@"\b0 "); break;
                case "i": case "em": rtf.Append(@"\i0 "); break;
                case "u": rtf.Append(@"\ulnone "); break;
                case "s": case "strike": case "del": rtf.Append(@"\strike0 "); break;
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    rtf.Append(@"\b0\fs22 \par "); break;
            }
        }

        private static int ListIndex(HtmlNode li)
        {
            int i = 0;
            foreach (var sibling in li.ParentNode.ChildNodes)
            {
                if (sibling.Name.ToLowerInvariant() == "li")
                {
                    i++;
                    if (sibling == li) return i;
                }
            }
            return 1;
        }
    }
}
