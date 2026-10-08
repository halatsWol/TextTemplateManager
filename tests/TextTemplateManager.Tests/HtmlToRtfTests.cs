using TextTemplateManager.Helpers;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>The RTF paste mode (and the RTF part of Auto). RTF is 7-bit and ignores raw line breaks, so
/// code blocks and any text outside plain ASCII need explicit escapes. The round trip through RtfPipe
/// (an independent RTF reader) checks that what we write reads back as the original text.</summary>
public class HtmlToRtfTests
{
    private static string ReadBack(string rtf) =>
        System.Net.WebUtility.HtmlDecode(RtfPipe.Rtf.ToHtml(rtf));

    [Fact]
    public void Code_block_lines_stay_separate_lines()
    {
        string rtf = HtmlConverter.ConvertHtmlToRtf("<pre><code>first line\nsecond line</code></pre>");

        Assert.Contains(@"first line\line second line", rtf);
        string back = ReadBack(rtf);
        Assert.DoesNotContain("first linesecond line", back);
        Assert.Contains("second line", back);
    }

    [Fact]
    public void Code_is_set_in_a_monospace_font()
    {
        string rtf = HtmlConverter.ConvertHtmlToRtf("<p>run <code>dir</code></p>");

        Assert.Contains("Consolas", rtf);
        Assert.Contains(@"{\f1 dir}", rtf);
    }

    [Fact]
    public void Tabs_inside_code_are_kept()
    {
        Assert.Contains(@"if\tab x", HtmlConverter.ConvertHtmlToRtf("<pre><code>if\tx</code></pre>"));
    }

    [Theory]
    [InlineData("Grüße aus Köln")]
    [InlineData("Ωμέγα и кириллица")]
    [InlineData("Emoji 😀 beyond the BMP")]
    [InlineData("€ – “quotes”")]
    public void Text_outside_ascii_survives(string text)
    {
        string rtf = HtmlConverter.ConvertHtmlToRtf($"<p>{text}</p>");

        Assert.All(rtf, ch => Assert.True(ch < 0x80, $"non-ASCII char U+{(int)ch:X4} written raw"));
        Assert.Contains(text, ReadBack(rtf));
    }

    [Fact]
    public void Rtf_control_characters_in_text_are_escaped()
    {
        string rtf = HtmlConverter.ConvertHtmlToRtf(@"<p>a\b {c}</p>");

        Assert.Contains(@"a\\b \{c\}", rtf);
        Assert.Contains(@"a\b {c}", ReadBack(rtf));
    }

    [Theory]
    [InlineData("<p>Hello</p><p>World</p>", @"\fs22 Hello\par World")]
    [InlineData("<h1>Title</h1><p>Text</p>", @"\fs22 \b\fs36 Title")]
    [InlineData("<ul><li>One</li><li>Two</li></ul>", @"\fs22 \bullet\tab One\par \bullet\tab Two")]
    [InlineData("<pre><code>code</code></pre>", @"\fs22 {\f1 code}")]
    public void The_paste_starts_with_the_first_block_not_an_empty_line(string html, string expected)
    {
        Assert.Contains(expected, HtmlConverter.ConvertHtmlToRtf(html));
    }

    [Fact]
    public void A_callout_panel_at_the_start_keeps_its_table_row()
    {
        string rtf = HtmlConverter.ConvertHtmlToRtf("<div data-panel-type=\"info\"><p>Note</p></div>");

        Assert.Contains(@"\fs22 \trowd", rtf);
        Assert.Contains(@"\pard\intbl", rtf);
    }

    [Fact]
    public void A_newline_outside_code_is_plain_whitespace()
    {
        Assert.Contains("one two", HtmlConverter.ConvertHtmlToRtf("<p>one\ntwo</p>"));
    }
}
