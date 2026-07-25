using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace TextTemplateManager.Helpers
{
    /// <summary>
    /// Two-stage HTML sanitizer for content that crosses the browser-connector boundary — both an
    /// incoming page selection stored as a template and the rendered content sent back out.
    ///
    /// Stage 1 (allow-list): only a fixed set of formatting elements and attributes survive; anything
    /// else is either unwrapped (tag dropped, its text kept) or, for active/remote-loading elements,
    /// removed with its contents. Stage 2 (deny-list): a second, independent pass over stage 1's output
    /// strips known-attack shapes in case anything slipped through — including markup a re-parse could
    /// resurrect (mutation-XSS).
    ///
    /// Both stages work on the parsed DOM and NEVER alter text nodes, so code samples — in any language,
    /// including HTML/JS shown as escaped text inside &lt;pre&gt;/&lt;code&gt; — pass through intact.
    /// (A code block's "&amp;lt;script&amp;gt;" is a text node, not a script element, so it is kept;
    /// only a *real* &lt;script&gt; element is dropped.)
    /// </summary>
    public static class HtmlSanitizer
    {
        // Formatting/structure elements the editor emits (plus close cousins). Kept — attributes are
        // still filtered below. Anything not here loses its tag, so a forgotten tag can never execute.
        private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "p", "div", "span", "br", "hr", "blockquote", "pre", "code",
            "h1", "h2", "h3", "h4", "h5", "h6",
            "strong", "b", "em", "i", "u", "s", "strike", "del", "ins", "mark",
            "sub", "sup", "small", "abbr", "cite", "q", "kbd", "samp", "var", "wbr",
            "ul", "ol", "li",
            "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "colgroup", "col",
            "a",
        };

        // Disallowed elements whose *contents* are also removed: they run script, load remote
        // resources, or hold non-display data. Every other disallowed tag is unwrapped instead (its
        // text survives). Listing the dangerous ones here keeps their inner payload from lingering as
        // stray text after an unwrap.
        private static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "iframe", "object", "embed", "applet", "noscript", "template",
            "link", "meta", "base", "title", "head", "form", "input", "button", "select", "option",
            "optgroup", "textarea", "frame", "frameset", "svg", "math", "canvas", "video", "audio",
            "source", "track", "img", "picture", "map", "area", "portal",
            // Legacy raw-text / parser-mode tags: a browser treats their contents as literal text, so
            // they must be dropped whole, never unwrapped (which could expose that text as live markup).
            "xmp", "plaintext", "listing", "noembed", "noframes",
        };

        // Attributes allowed on any element: inert and formatting-only.
        private static readonly HashSet<string> GlobalAttrs = new(StringComparer.OrdinalIgnoreCase)
        {
            "class", "style", "title", "dir", "lang", "align",
        };

        // Extra attributes allowed on specific tags (links, lists, table cells).
        private static readonly Dictionary<string, HashSet<string>> TagAttrs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new(StringComparer.OrdinalIgnoreCase) { "href", "target", "rel" },
            ["ol"] = new(StringComparer.OrdinalIgnoreCase) { "start", "type" },
            ["td"] = new(StringComparer.OrdinalIgnoreCase) { "colspan", "rowspan", "colwidth", "headers", "scope", "valign", "width" },
            ["th"] = new(StringComparer.OrdinalIgnoreCase) { "colspan", "rowspan", "colwidth", "headers", "scope", "valign", "width" },
            ["col"] = new(StringComparer.OrdinalIgnoreCase) { "span", "width" },
            ["colgroup"] = new(StringComparer.OrdinalIgnoreCase) { "span", "width" },
        };

        // URL schemes allowed in href (everything else — javascript:, vbscript:, data:, file:, … — is
        // stripped). A relative URL or #anchor has no scheme and is allowed.
        private static readonly string[] AllowedSchemes = { "http:", "https:", "mailto:", "tel:" };

        // CSS that can execute or load remote content; if a style value matches, the whole attribute
        // is dropped. The editor's own styles (color / background-color / text-align / width) never do.
        private static readonly Regex DangerousStyle = new(
            @"expression\s*\(|javascript\s*:|vbscript\s*:|-moz-binding|behavior\s*:|@import|url\s*\(\s*['""]?\s*(?:javascript|vbscript|data)\s*:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Sanitize(string? html)
        {
            if (string.IsNullOrEmpty(html)) return html ?? "";
            // Allow-list first (keep only known-good), then a deny-list hardening pass over that output.
            return DenyListPass(AllowListPass(html));
        }

        // ---- Stage 1: allow-list ----
        private static string AllowListPass(string html)
        {
            HtmlDocument doc;
            try { doc = new HtmlDocument(); doc.LoadHtml(html); }
            catch { return ""; }   // unparseable -> store nothing rather than raw markup

            SanitizeNode(doc.DocumentNode);
            return doc.DocumentNode.OuterHtml;
        }

        // Depth-first: keep text verbatim, drop comments, remove active elements with their contents,
        // filter the attributes of allowed elements, and unwrap everything else (tag out, text kept).
        private static void SanitizeNode(HtmlNode parent)
        {
            foreach (var child in parent.ChildNodes.ToList())
            {
                switch (child.NodeType)
                {
                    case HtmlNodeType.Comment:
                        child.Remove();
                        break;

                    case HtmlNodeType.Text:
                        break;   // code samples live here — never altered

                    case HtmlNodeType.Element:
                        string name = child.Name.ToLowerInvariant();
                        if (DropWithContent.Contains(name))
                        {
                            child.Remove();
                        }
                        else if (AllowedTags.Contains(name))
                        {
                            FilterAttributes(child, name);
                            SanitizeNode(child);
                        }
                        else
                        {
                            SanitizeNode(child);     // clean the subtree first
                            Unwrap(parent, child);   // then strip the disallowed tag, keeping its text
                        }
                        break;
                }
            }
        }

        // Replace an element with its (already-sanitized) children, preserving order.
        private static void Unwrap(HtmlNode parent, HtmlNode element)
        {
            foreach (var kid in element.ChildNodes.ToList())
            {
                kid.Remove();
                parent.InsertBefore(kid, element);
            }
            element.Remove();
        }

        private static void FilterAttributes(HtmlNode el, string tag)
        {
            foreach (var attr in el.Attributes.ToList())
            {
                string an = attr.Name.ToLowerInvariant();

                if (!IsAttrAllowed(tag, an)) { attr.Remove(); continue; }
                if (an == "style")
                {
                    // Rebuild the style from an allow-list of inert CSS properties: drops url()
                    // (remote fetch / tracking), expression()/-moz-binding (legacy script), and any
                    // value with quotes/angle brackets (attribute break-out). Empty -> drop the attr.
                    string cleaned = SanitizeStyle(attr.Value);
                    if (cleaned.Length == 0) attr.Remove(); else attr.Value = cleaned;
                    continue;
                }
                if (an == "href" && !IsAllowedUrl(attr.Value)) { attr.Remove(); continue; }
            }

            // A link that opens a new browsing context gets rel="noopener noreferrer" so the opened
            // page can't script this one back through window.opener (reverse tab-nabbing).
            if (tag == "a" && el.Attributes.Contains("target"))
                EnsureSafeRel(el);
        }

        private static bool IsAttrAllowed(string tag, string attr)
        {
            if (attr.StartsWith("on", StringComparison.Ordinal)) return false;   // onclick/onerror/onload/…
            if (attr.StartsWith("data-", StringComparison.Ordinal)) return true; // inert; carries panel/codeblock state
            if (GlobalAttrs.Contains(attr)) return true;
            return TagAttrs.TryGetValue(tag, out var set) && set.Contains(attr);
        }

        private static bool IsAllowedUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;   // empty/relative
            // Decode entities and drop whitespace/NULs that could hide the scheme (e.g. "java\0script:").
            string v = WebUtility.HtmlDecode(value);
            v = new string(v.Where(c => !char.IsWhiteSpace(c) && c != '\0').ToArray());

            int colon = v.IndexOf(':');
            if (colon < 0) return true;                          // no scheme -> relative/anchor/query
            int slash = v.IndexOf('/');
            if (slash >= 0 && slash < colon) return true;        // ':' is in the path, not a scheme

            return AllowedSchemes.Any(s => v.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        }

        // Inline CSS properties kept in a style attribute — inert formatting only; covers everything
        // the editor emits (color / highlight background-color / text-align / column width).
        private static readonly HashSet<string> SafeStyleProps = new(StringComparer.OrdinalIgnoreCase)
        {
            "color", "background-color", "text-align", "width", "height",
            "font-weight", "font-style", "text-decoration", "text-decoration-line", "vertical-align",
        };

        // Rebuild a style value from only the allow-listed declarations, dropping any whose value could
        // fetch remotely, execute, or break out of the attribute.
        private static string SanitizeStyle(string? style)
        {
            if (string.IsNullOrWhiteSpace(style)) return "";

            var kept = new List<string>();
            foreach (var decl in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = decl.IndexOf(':');
                if (colon <= 0) continue;
                string prop = decl.Substring(0, colon).Trim().ToLowerInvariant();
                string val = decl.Substring(colon + 1).Trim();
                if (val.Length == 0 || !SafeStyleProps.Contains(prop) || HasUnsafeStyleValue(val)) continue;
                kept.Add($"{prop}: {val}");
            }
            return string.Join("; ", kept);
        }

        private static bool HasUnsafeStyleValue(string val)
        {
            // Quotes/angle brackets/backslashes could break the attribute or smuggle markup; braces and
            // comments could hide extra rules. rgb()/#hex/keywords/lengths need none of these.
            if (val.IndexOfAny(new[] { '"', '\'', '<', '>', '\\', '{', '}' }) >= 0) return true;
            string v = val.ToLowerInvariant();
            return v.Contains("url(") || v.Contains("expression") || v.Contains("javascript:")
                || v.Contains("vbscript:") || v.Contains("-moz-binding") || v.Contains("behavior:")
                || v.Contains("@import") || v.Contains("/*");
        }

        private static void EnsureSafeRel(HtmlNode a)
        {
            var tokens = (a.GetAttributeValue("rel", "") ?? "")
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant())
                .ToList();
            if (!tokens.Contains("noopener")) tokens.Add("noopener");
            if (!tokens.Contains("noreferrer")) tokens.Add("noreferrer");
            a.SetAttributeValue("rel", string.Join(" ", tokens));
        }

        // ---- Stage 2: deny-list hardening ----

        // Elements removed outright (with contents) if they somehow survive stage 1 — the usual active
        // tags plus legacy/parser-mode tags (xmp, plaintext, listing, marquee, …) known to enable mXSS.
        private static readonly HashSet<string> DenyTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "base",
            "basefont", "meta", "link", "form", "svg", "math", "noscript", "template", "title", "head",
            "xml", "isindex", "marquee", "blink", "listing", "xmp", "plaintext", "portal",
        };

        // Executable / HTML-bearing URL schemes. A value that starts with one of these (after entities
        // and hidden whitespace/control chars are stripped) is treated as an attack and dropped.
        private static readonly string[] DangerousSchemes =
        {
            "javascript:", "vbscript:", "livescript:", "mocha:",
            "data:text/html", "data:application", "data:image/svg",
        };

        private static string DenyListPass(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            HtmlDocument doc;
            try { doc = new HtmlDocument(); doc.LoadHtml(html); }
            catch { return ""; }

            // Drop denied elements (with contents) and any comments (conditional comments are an mXSS
            // vector). Re-parsing here is deliberate: it catches markup that stage 1's serialization
            // could round-trip back into a live element.
            foreach (var node in doc.DocumentNode.Descendants()
                         .Where(n => n.NodeType == HtmlNodeType.Comment ||
                                     (n.NodeType == HtmlNodeType.Element && DenyTags.Contains(n.Name.ToLowerInvariant())))
                         .ToList())
            {
                if (node.ParentNode != null) node.Remove();
            }

            // Strip dangerous attributes from whatever remains: event handlers, script-bearing CSS,
            // and any value whose (decoded) scheme is executable.
            foreach (var el in doc.DocumentNode.Descendants()
                         .Where(n => n.NodeType == HtmlNodeType.Element).ToList())
            {
                foreach (var attr in el.Attributes.ToList())
                {
                    string an = attr.Name.ToLowerInvariant();
                    if (an.StartsWith("on", StringComparison.Ordinal)) { attr.Remove(); continue; }
                    if (an == "style" && DangerousStyle.IsMatch(attr.Value ?? "")) { attr.Remove(); continue; }
                    if (StartsWithDangerousScheme(DecodeForScan(attr.Value))) attr.Remove();
                }
            }

            return doc.DocumentNode.OuterHtml;
        }

        // Decode entities and remove whitespace/control/NUL so an obfuscated scheme
        // ("java\0script:", "&#106;avascript:", "jav&#x09;ascript:") can't hide from the scheme check.
        private static string DecodeForScan(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string v = WebUtility.HtmlDecode(value);
            return new string(v.Where(c => !char.IsWhiteSpace(c) && c != '\0' && !char.IsControl(c)).ToArray());
        }

        private static bool StartsWithDangerousScheme(string decoded) =>
            DangerousSchemes.Any(s => decoded.StartsWith(s, StringComparison.OrdinalIgnoreCase));
    }
}
