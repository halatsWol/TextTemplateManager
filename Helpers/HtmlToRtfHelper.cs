using HtmlAgilityPack;
using System.Text;

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
            // Colour table for callout panels: pairs of (background, accent) per type, indices 1..10.
            rtf.Append(@"{\colortbl;")
               .Append(@"\red222\green235\blue255;\red38\green132\blue255;")   // 1,2  info
               .Append(@"\red234\green230\blue255;\red101\green84\blue192;")   // 3,4  note
               .Append(@"\red227\green252\blue239;\red54\green179\blue126;")   // 5,6  success
               .Append(@"\red255\green250\blue230;\red255\green171\blue0;")    // 7,8  warning
               .Append(@"\red255\green235\blue230;\red255\green86\blue48;}");  // 9,10 error
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

            // Callout panel -> a shaded single-cell table with a left accent border.
            if (name == "div" && !string.IsNullOrEmpty(node.GetAttributeValue("data-panel-type", "")))
            {
                WritePanel(node, rtf);
                return;
            }

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
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                    rtf.Append(@"\b0\fs22 \par "); break;
            }
        }

        /// <summary>Renders a callout panel as a one-row, one-cell table: cell background = the
        /// panel tint, a thick left border in the accent colour, and a bold accent-coloured label.
        /// A single cell keeps all the panel's text in one box (Word/WordPad/Outlook render this).</summary>
        private static void WritePanel(HtmlNode node, StringBuilder rtf)
        {
            var (bg, accent, label) = PanelColors(node.GetAttributeValue("data-panel-type", "info"));

            rtf.Append(@"\par ");
            rtf.Append(@"\trowd\trgaph108\trleft0");
            rtf.Append(@"\clcbpat").Append(bg);                          // cell background
            rtf.Append(@"\clbrdrl\brdrs\brdrw60\brdrcf").Append(accent); // left accent rail
            rtf.Append(@"\clbrdrt\brdrs\brdrw10\brdrcf").Append(bg);
            rtf.Append(@"\clbrdrb\brdrs\brdrw10\brdrcf").Append(bg);
            rtf.Append(@"\clbrdrr\brdrs\brdrw10\brdrcf").Append(bg);
            rtf.Append(@"\cellx9360");
            rtf.Append(@"\pard\intbl\sb40\sa40 ");
            rtf.Append(@"{\b\cf").Append(accent).Append(' ').Append(label).Append(@"}\line ");
            WriteCellContent(node, rtf);
            rtf.Append(@"\cell\row\pard ");
        }

        /// <summary>Writes a panel's block content into a table cell, joining paragraphs and list
        /// items with soft line breaks so it all stays inside the one cell.</summary>
        private static void WriteCellContent(HtmlNode panel, StringBuilder rtf)
        {
            foreach (var child in panel.ChildNodes)
            {
                string cn = child.Name.ToLowerInvariant();
                if (cn == "ul" || cn == "ol")
                {
                    int n = 0;
                    foreach (var li in child.ChildNodes)
                    {
                        if (li.Name.ToLowerInvariant() != "li") continue;
                        n++;
                        rtf.Append(@"\line ").Append(cn == "ol" ? n + @".\tab " : @"\bullet\tab ");
                        foreach (var gc in li.ChildNodes) ProcessNode(gc, rtf);
                    }
                }
                else if (cn == "p")
                {
                    rtf.Append(@"\line ");
                    foreach (var gc in child.ChildNodes) ProcessNode(gc, rtf);
                }
                else
                {
                    ProcessNode(child, rtf);
                }
            }
        }

        private static (int Bg, int Accent, string Label) PanelColors(string type) => type.ToLowerInvariant() switch
        {
            "note" => (3, 4, "Note"),
            "success" => (5, 6, "Success"),
            "warning" => (7, 8, "Warning"),
            "error" => (9, 10, "Error"),
            _ => (1, 2, "Info"),
        };

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
