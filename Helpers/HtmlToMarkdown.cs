using HtmlAgilityPack;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TextTemplateManager.Helpers
{
    /// <summary>
    /// Converts the editor's HTML into Markdown source text (for the Markdown paste mode).
    /// Covers the formatting the editor can produce: headings, bold/italic/strike, code,
    /// links, nested bullet/numbered lists, and GitHub-flavored tables.
    /// </summary>
    public static class HtmlToMarkdown
    {
        public static string Convert(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var sb = new StringBuilder();
            foreach (var node in doc.DocumentNode.ChildNodes)
                WriteNode(node, sb, listOrdered: false, depth: 0);

            // Collapse excess blank lines and trim.
            var text = sb.ToString().Replace("\r\n", "\n");
            while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
            return text.Trim();
        }

        private static void WriteNode(HtmlNode node, StringBuilder sb, bool listOrdered, int depth)
        {
            if (node.NodeType == HtmlNodeType.Text)
            {
                sb.Append(System.Net.WebUtility.HtmlDecode(node.InnerText));
                return;
            }

            switch (node.Name.ToLowerInvariant())
            {
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                    int level = node.Name[1] - '0';
                    sb.Append('\n').Append(new string('#', level)).Append(' ');
                    WriteChildren(node, sb, listOrdered, depth);
                    sb.Append("\n\n");
                    break;

                case "p":
                    WriteChildren(node, sb, listOrdered, depth);
                    sb.Append("\n\n");
                    break;

                case "br":
                    sb.Append("  \n");
                    break;

                case "strong":
                case "b":
                    AppendInlineMark(node, sb, "**", depth);
                    break;

                case "em":
                case "i":
                    AppendInlineMark(node, sb, "*", depth);
                    break;

                case "s":
                case "del":
                case "strike":
                    AppendInlineMark(node, sb, "~~", depth);
                    break;

                case "code":
                    AppendInlineMark(node, sb, "`", depth);
                    break;

                // Markdown has no sub/superscript; GFM renders raw inline HTML, so pass it through.
                case "sub":
                    sb.Append("<sub>"); WriteChildren(node, sb, listOrdered, depth); sb.Append("</sub>");
                    break;

                case "sup":
                    sb.Append("<sup>"); WriteChildren(node, sb, listOrdered, depth); sb.Append("</sup>");
                    break;

                case "pre":
                    string preText = System.Net.WebUtility.HtmlDecode(node.InnerText)
                        .Replace("\r\n", "\n").Trim('\n');
                    if (node.GetAttributeValue("data-line-numbers", "") == "true")
                    {
                        // Code block -> fenced.
                        sb.Append("\n```\n").Append(preText).Append("\n```\n\n");
                    }
                    else
                    {
                        // Preformat -> indented block (4 spaces), distinct from a fenced code block.
                        sb.Append('\n');
                        foreach (var line in preText.Split('\n'))
                            sb.Append("    ").Append(line).Append('\n');
                        sb.Append('\n');
                    }
                    break;

                case "blockquote":
                    var quote = new StringBuilder();
                    WriteChildren(node, quote, listOrdered, depth);
                    sb.Append('\n');
                    foreach (var line in quote.ToString().Replace("\r\n", "\n").TrimEnd().Split('\n'))
                        sb.Append("> ").Append(line).Append('\n');
                    sb.Append('\n');
                    break;

                case "hr":
                    sb.Append("\n---\n\n");
                    break;

                case "a":
                    string href = node.GetAttributeValue("href", string.Empty);
                    sb.Append('['); WriteChildren(node, sb, listOrdered, depth); sb.Append("](").Append(href).Append(')');
                    break;

                case "ul":
                    WriteList(node, sb, ordered: false, depth);
                    sb.Append('\n');
                    break;

                case "ol":
                    WriteList(node, sb, ordered: true, depth);
                    sb.Append('\n');
                    break;

                case "li": // fallback; lists are normally rendered via WriteList
                    sb.Append(new string(' ', depth * 2)).Append(listOrdered ? "1. " : "- ");
                    WriteListItem(node, sb, depth);
                    sb.Append('\n');
                    break;

                case "table":
                    WriteTable(node, sb);
                    break;

                case "div":
                    string panelType = node.GetAttributeValue("data-panel-type", "");
                    if (!string.IsNullOrEmpty(panelType)) WritePanel(node, sb, panelType, depth);
                    else WriteChildren(node, sb, listOrdered, depth);
                    break;

                default:
                    WriteChildren(node, sb, listOrdered, depth);
                    break;
            }
        }

        /// <summary>Writes a callout panel as a blockquote with an emoji + bold label first line —
        /// the most broadly-rendered way to convey a callout in Markdown (GitHub, GitLab, Obsidian,
        /// and plain renderers all show it as a labeled quote).</summary>
        private static void WritePanel(HtmlNode node, StringBuilder sb, string panelType, int depth)
        {
            var (_, _, emoji, label) = PanelHtml.StyleFor(panelType);
            var inner = new StringBuilder();
            WriteChildren(node, inner, listOrdered: false, depth);

            sb.Append('\n').Append("> ").Append(emoji).Append(" **").Append(label).Append("**\n>\n");
            foreach (var line in inner.ToString().Replace("\r\n", "\n").TrimEnd().Split('\n'))
                sb.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
            sb.Append('\n');
        }

        /// <summary>Writes a table as a GitHub-flavored Markdown table. Since the editor's tables
        /// have no header row, the first row is promoted to the header (GFM requires one).</summary>
        private static void WriteTable(HtmlNode table, StringBuilder sb)
        {
            var rows = table.Descendants("tr")
                .Select(tr => tr.ChildNodes.Where(c => c.Name is "td" or "th").Select(RenderCell).ToList())
                .Where(cells => cells.Count > 0)
                .ToList();
            if (rows.Count == 0) return;

            int cols = rows.Max(r => r.Count);

            sb.Append('\n');
            for (int r = 0; r < rows.Count; r++)
            {
                var cells = Enumerable.Range(0, cols)
                    .Select(c => c < rows[r].Count ? rows[r][c] : string.Empty);
                sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");

                if (r == 0) // header separator
                    sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", cols))).Append(" |\n");
            }
            sb.Append('\n');
        }

        /// <summary>Renders a single table cell to inline Markdown: block content is flattened to
        /// one line (Markdown table cells cannot span lines) and pipes are escaped.</summary>
        private static string RenderCell(HtmlNode cell)
        {
            var parts = new List<string>();
            foreach (var child in cell.ChildNodes)
            {
                var inner = new StringBuilder();
                if (child.NodeType == HtmlNodeType.Text)
                    inner.Append(System.Net.WebUtility.HtmlDecode(child.InnerText));
                else if (child.Name == "p")
                    WriteChildren(child, inner, listOrdered: false, depth: 0); // inline, no blank line
                else
                    WriteNode(child, inner, listOrdered: false, depth: 0);

                string piece = inner.ToString();
                if (!string.IsNullOrWhiteSpace(piece)) parts.Add(piece);
            }

            string s = Regex.Replace(string.Join(" ", parts), @"\s+", " ").Trim();
            return s.Replace("|", "\\|");
        }

        /// <summary>Writes an ordered/bullet list: ordered items are numbered sequentially and
        /// items stay on consecutive lines (no blank line between them).</summary>
        private static void WriteList(HtmlNode list, StringBuilder sb, bool ordered, int depth)
        {
            int number = 1;
            foreach (var li in list.ChildNodes)
            {
                if (li.Name != "li") continue;
                sb.Append(new string(' ', depth * 2));
                sb.Append(ordered ? $"{number}. " : "- ");
                number++;
                WriteListItem(li, sb, depth);
                sb.Append('\n');
            }
        }

        /// <summary>Writes the inline content of a single list item. A wrapping &lt;p&gt; is
        /// rendered inline (no blank line), and a nested list is emitted indented on new lines.</summary>
        private static void WriteListItem(HtmlNode li, StringBuilder sb, int depth)
        {
            foreach (var child in li.ChildNodes)
            {
                if (child.Name is "ul" or "ol")
                {
                    sb.Append('\n');
                    WriteList(child, sb, ordered: child.Name == "ol", depth + 1);
                }
                else if (child.Name == "p")
                {
                    WriteChildren(child, sb, listOrdered: false, depth); // inline, no trailing blank line
                }
                else
                {
                    WriteNode(child, sb, listOrdered: false, depth);
                }
            }
        }

        /// <summary>Wraps a node's rendered children in an emphasis marker, moving any leading/
        /// trailing whitespace OUTSIDE the markers — Markdown requires the character adjacent to a
        /// marker to be non-whitespace, so "**bold **" would render literally.</summary>
        private static void AppendInlineMark(HtmlNode node, StringBuilder sb, string marker, int depth)
        {
            var inner = new StringBuilder();
            WriteChildren(node, inner, listOrdered: false, depth);
            string s = inner.ToString();

            int start = 0, end = s.Length;
            while (start < end && char.IsWhiteSpace(s[start])) start++;
            while (end > start && char.IsWhiteSpace(s[end - 1])) end--;

            if (end <= start) { sb.Append(s); return; } // whitespace-only: emit as-is, no emphasis

            sb.Append(s, 0, start);                                     // leading whitespace, outside markers
            sb.Append(marker).Append(s, start, end - start).Append(marker);
            sb.Append(s, end, s.Length - end);                         // trailing whitespace, outside markers
        }

        private static void WriteChildren(HtmlNode node, StringBuilder sb, bool listOrdered, int depth)
        {
            foreach (var child in node.ChildNodes)
            {
                // A nested list inside a list item indents one level deeper.
                bool nested = child.Name is "ul" or "ol";
                WriteNode(child, sb, listOrdered, nested ? depth + 1 : depth);
            }
        }
    }
}
