using System;
using System.Net;
using System.Text.RegularExpressions;

namespace TextTemplateManager.Helpers
{
    /// <summary>
    /// Small helpers for the HTML content produced by the WebView (TipTap) editor.
    /// </summary>
    public static class HtmlUtils
    {
        private static readonly Regex BreakTags = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BlockEnds = new(@"</(p|div|li|tr|h[1-6]|blockquote|pre)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnyTag = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex ManyNewlines = new(@"\n{3,}", RegexOptions.Compiled);

        // A leading empty block: a <p>/<div> at the very start whose only content is whitespace,
        // &nbsp;, or <br> tags — i.e. a blank line the user left above the real content.
        private static readonly Regex LeadingEmptyBlock = new(
            @"\A\s*<(p|div)(?:\s[^>]*)?>(?:\s|&nbsp;|&#160;| |<br\s*/?>)*</\1>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches rgb(…) / rgba(…) colour functions with 0-255 (or 0-1 alpha) components.
        private static readonly Regex RgbFunc = new(
            @"rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+\s*)?\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Rewrites CSS <c>rgb()</c>/<c>rgba()</c> colour values to <c>#rrggbb</c> hex. TipTap
        /// emits colours as <c>rgb(…)</c>; hex is the most universally-parsed form across paste
        /// targets, and any alpha channel is dropped (opaque). Non-matching text is untouched.
        /// </summary>
        public static string NormalizeColorsToHex(string? html)
        {
            if (string.IsNullOrEmpty(html)) return html ?? string.Empty;

            return RgbFunc.Replace(html, m =>
            {
                if (!byte.TryParse(m.Groups[1].Value, out byte r) ||
                    !byte.TryParse(m.Groups[2].Value, out byte g) ||
                    !byte.TryParse(m.Groups[3].Value, out byte b))
                {
                    return m.Value; // component out of 0-255 range; leave as-is
                }
                return $"#{r:x2}{g:x2}{b:x2}";
            });
        }

        /// <summary>
        /// Converts editor HTML to reasonable plain text: block elements and &lt;br&gt; become
        /// line breaks, remaining tags are stripped, and entities are decoded.
        /// </summary>
        public static string ToPlainText(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            string text = html;
            text = BreakTags.Replace(text, "\n");
            text = BlockEnds.Replace(text, "$0\n"); // keep tag then add newline; stripped next
            text = AnyTag.Replace(text, string.Empty);
            text = WebUtility.HtmlDecode(text);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = ManyNewlines.Replace(text, "\n\n");
            return text.Trim();
        }

        /// <summary>
        /// Removes blank lines above the first real content: leading empty &lt;p&gt;/&lt;div&gt;
        /// blocks (only whitespace/&nbsp;/&lt;br&gt;) and any leading whitespace. Content that is
        /// entirely blank collapses to an empty string.
        /// </summary>
        public static string StripLeadingEmptyLines(string? html)
        {
            if (string.IsNullOrEmpty(html)) return html ?? string.Empty;

            string result = html;
            Match m;
            while ((m = LeadingEmptyBlock.Match(result)).Success && m.Length > 0)
                result = result.Substring(m.Length);

            return result.TrimStart();
        }

        public static bool LooksLikeRtf(string? content)
            => content != null && content.TrimStart().StartsWith(@"{\rtf1", StringComparison.OrdinalIgnoreCase);
    }
}
