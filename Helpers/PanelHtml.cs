using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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

        // A `color:` CSS property — but not background-color / border-color / --custom-palette-color.
        private static readonly Regex ColorStyle =
            new(@"(?<![\w-])color\s*:\s*(#[0-9a-fA-F]{3,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>HTML/Jira mode: rewrite panels and coloured text to the structures Atlassian's
        /// editor rebuilds on paste — <c>div[data-panel-type]</c> for panels and the text-colour
        /// mark for colours. Panels are wrapped as a closed slice so a leading one is not unwrapped.</summary>
        public static string Prepare(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return html ?? string.Empty;

            bool hasPanel = HasPanel(html);
            bool hasColor = ColorStyle.IsMatch(html);
            if (!hasPanel && !hasColor) return html!; // nothing Jira-specific to normalize

            var doc = Load(html!);

            foreach (var panel in Panels(doc))
            {
                NormalizeType(panel);
                panel.SetAttributeValue("class", "ak-editor-panel"); // drop the app-only ttm-panel class

                // Jira's panel node holds block content; wrap bare inline content in a paragraph.
                if (!panel.ChildNodes.Any(n => n.NodeType == HtmlNodeType.Element))
                    panel.InnerHtml = "<p>" + panel.InnerHtml + "</p>";
            }

            if (hasColor) ApplyJiraTextColors(doc);

            string outHtml = doc.DocumentNode.OuterHtml;
            // The closed-slice wrapper only matters for a leading panel; skip it for colour-only
            // content so inline coloured text still merges at the paste point.
            return hasPanel ? ClosedSlice(outHtml) : outHtml;
        }

        /// <summary>Rewrites the editor's coloured spans (<c>style="color:#hex"</c>) into Jira's
        /// text-colour mark (<c>data-text-custom-color</c> + <c>fabric-text-color-mark</c> class).
        /// Jira only accepts colours from its fixed palette, so each colour is snapped to the nearest
        /// palette entry; near-neutral colours are left as Jira's default text.</summary>
        private static void ApplyJiraTextColors(HtmlDocument doc)
        {
            var spans = doc.DocumentNode.SelectNodes("//span[contains(@style,'color')]");
            if (spans == null) return;

            foreach (var span in spans)
            {
                var m = ColorStyle.Match(span.GetAttributeValue("style", ""));
                if (!m.Success) continue;

                string? jira = NearestPaletteColor(m.Groups[1].Value);
                if (jira == null) continue; // near-neutral — leave as default text colour

                span.SetAttributeValue("data-text-custom-color", jira);
                string cls = span.GetAttributeValue("class", "");
                span.SetAttributeValue("class",
                    string.IsNullOrEmpty(cls) ? "fabric-text-color-mark" : cls + " fabric-text-color-mark");
                span.SetAttributeValue("style", $"color: {jira}; --custom-palette-color: {jira};");
            }
        }

        // Atlassian's editor text-colour palette — the only colours its paste accepts.
        // Grey is #97a0af (--ds-icon-accent-gray); Jira's "Default" text has no mark (see below).
        private static readonly string[] JiraPalette =
        {
            "#0747a6", "#008da6", "#006644", "#ff991f", "#bf2600", "#403294", // bold
            "#4c9aff", "#00b8d9", "#36b37e", "#ffc400", "#ff5630", "#6554c0", // standard
            "#b3d4ff", "#b3f5ff", "#abf5d1", "#fff0b3", "#ffbdad", "#eae6ff", // subtle
            "#97a0af", "#ffffff",                                            // grey, white
        };

        // Dark, near-neutral colours (black / dark gray) snap to Jira's default text (no colour
        // mark); mid and light grays map to the palette's grey above.
        private static readonly string[] JiraNeutral =
        {
            "#000000", "#172b4d", "#253858", "#42526e",
        };

        /// <summary>Nearest Jira palette colour to <paramref name="hex"/> by CIELAB (perceptual)
        /// distance, or <c>null</c> when the closest match is a neutral (leave text default). CIELAB
        /// separates by lightness, so e.g. a bright red and a dark red map to different palette reds
        /// (plain RGB distance collapses both onto the darker one).</summary>
        private static string? NearestPaletteColor(string hex)
        {
            var (tl, ta, tb) = ToLab(hex);
            string? best = null;
            double bestDist = double.MaxValue;
            bool bestNeutral = false;

            void Consider(string cand, bool neutral)
            {
                var (cl, ca, cb) = ToLab(cand);
                double dl = tl - cl, da = ta - ca, db = tb - cb;
                double d = dl * dl + da * da + db * db;
                if (d < bestDist) { bestDist = d; best = cand; bestNeutral = neutral; }
            }

            foreach (var c in JiraPalette) Consider(c, false);
            foreach (var c in JiraNeutral) Consider(c, true);
            return bestNeutral ? null : best;
        }

        /// <summary>Converts an #rrggbb / #rgb colour to CIELAB (sRGB, D65) for perceptual comparison.</summary>
        private static (double L, double a, double b) ToLab(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
            double r = Linear(Convert.ToInt32(hex.Substring(0, 2), 16));
            double g = Linear(Convert.ToInt32(hex.Substring(2, 2), 16));
            double b = Linear(Convert.ToInt32(hex.Substring(4, 2), 16));

            double x = F((0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047);
            double y = F((0.2126 * r + 0.7152 * g + 0.0722 * b) / 1.0);
            double z = F((0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883);
            return (116 * y - 16, 500 * (x - y), 200 * (y - z));

            static double Linear(int c)
            {
                double v = c / 255.0;
                return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116.0;
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
