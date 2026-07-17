using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

// Build-time generator: renders docs/Manual.md to a PDF next to the app exe.
// Args: <input.md> <output.pdf> [version]
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ManualGen <input.md> <output.pdf> [version]");
    return 1;
}

string input = args[0];
string output = args[1];
string version = args.Length >= 3 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : "0.0.0-dev";
if (!File.Exists(input))
{
    Console.Error.WriteLine($"ManualGen: input not found: {input}");
    return 1;
}

QuestPDF.Settings.License = LicenseType.Community;

// Lato (QuestPDF's bundled font) has no U+25B8 glyph — use a plain '>' for menu paths.
string raw = File.ReadAllText(input).Replace("▸", ">");
var pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();
var document = Markdown.Parse(raw, pipeline);

// Image paths in the markdown are resolved relative to the markdown file.
ManualContext.ImageBaseDir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? "";

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

Document.Create(container =>
{
    container.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontSize(11).LineHeight(1.35f));

        page.Header().Text("Text Template Manager — Manual")
            .SemiBold().FontColor(Colors.Grey.Darken2);

        page.Content().Column(col =>
        {
            // Cover / title block (once, at the top of the first page).
            col.Item().Text("Text Template Manager").FontSize(26).Bold().FontColor(Colors.Grey.Darken4);
            col.Item().Text("User Manual").FontSize(14).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(6).Text($"Version {version}   ·   Marflow Software")
                .FontSize(10).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(12).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);

            RenderBlocks(col, document);
        });

        page.Footer().AlignCenter().Text(t =>
        {
            t.CurrentPageNumber();
            t.Span(" / ");
            t.TotalPages();
        });
    });
}).GeneratePdf(output);

Console.WriteLine($"ManualGen: {output}");
return 0;

static void RenderBlocks(ColumnDescriptor col, ContainerBlock container)
{
    foreach (var block in container)
        RenderBlock(col, block);
}

static void RenderBlock(ColumnDescriptor col, Block block)
{
    switch (block)
    {
        case HeadingBlock h:
            float size = h.Level <= 2 ? 16f : 13f;
            // Orphan control: EnsureSpace requires room for the heading plus a few following
            // lines; if that doesn't fit, the heading moves to the top of the next page.
            col.Item().PaddingTop(h.Level <= 2 ? 14f : 9f).EnsureSpace(64).Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(size).SemiBold().FontColor(Colors.Grey.Darken4));
                RenderInline(t, h.Inline, default);
            });
            break;

        case ParagraphBlock p:
            RenderParagraph(col, p);
            break;

        case ListBlock list:
            int n = 1;
            foreach (var child in list)
            {
                if (child is not ListItemBlock item) continue;
                string marker = list.IsOrdered ? $"{n++}." : "•";
                col.Item().PaddingTop(3).Row(row =>
                {
                    row.ConstantItem(16).Text(marker).FontColor(Colors.Grey.Darken2);
                    row.RelativeItem().Column(inner =>
                    {
                        foreach (var b in item)
                            if (b is ParagraphBlock pb)
                            {
                                if (HasImage(pb.Inline)) RenderParagraphWithImages(inner, pb.Inline);
                                else inner.Item().Text(t => RenderInline(t, pb.Inline, default));
                            }
                            else RenderBlock(inner, b);
                    });
                });
            }
            break;

        case Table table:
            var rows = table.OfType<TableRow>().ToList();
            if (rows.Count == 0) break;
            int cols = rows.Max(r => r.Count);
            // EnsureSpace keeps the header with the first rows; Table.Header repeats the header
            // automatically whenever the table spans a page break.
            col.Item().PaddingTop(8).EnsureSpace(70).Table(t =>
            {
                t.ColumnsDefinition(c => { for (int i = 0; i < cols; i++) c.RelativeColumn(); });

                var header = rows.FirstOrDefault(r => r.IsHeader) ?? rows[0];
                t.Header(h =>
                {
                    foreach (var cell in header.OfType<TableCell>())
                        h.Cell().Element(HeaderCell).Text(tx =>
                        {
                            tx.DefaultTextStyle(x => x.SemiBold());
                            RenderInline(tx, CellInline(cell), default);
                        });
                });

                foreach (var row in rows)
                {
                    if (row.IsHeader) continue;
                    foreach (var cell in row.OfType<TableCell>())
                        t.Cell().Element(BodyCell).Text(tx => RenderInline(tx, CellInline(cell), default));
                }
            });
            break;

        case ThematicBreakBlock:
            col.Item().PaddingVertical(8).LineHorizontal(0.75f).LineColor(Colors.Grey.Lighten2);
            break;

        case QuoteBlock q:
            col.Item().PaddingTop(6).PaddingLeft(12).Column(inner => RenderBlocks(inner, q));
            break;

        default:
            if (block is LeafBlock leaf && leaf.Inline != null)
                col.Item().PaddingTop(6).Text(t => RenderInline(t, leaf.Inline, default));
            break;
    }
}

static IContainer HeaderCell(IContainer c) =>
    c.Background(Colors.Grey.Lighten3).Border(0.5f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(4).PaddingHorizontal(6);

static IContainer BodyCell(IContainer c) =>
    c.Border(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4).PaddingHorizontal(6);

static ContainerInline? CellInline(TableCell cell) =>
    (cell.FirstOrDefault() as ParagraphBlock)?.Inline;

static void RenderInline(TextDescriptor text, ContainerInline? inline, InlineStyle style)
{
    if (inline == null) return;
    foreach (var node in inline)
        RenderInlineNode(text, node, style);
}

static void RenderInlineNode(TextDescriptor text, Inline node, InlineStyle style)
{
    switch (node)
    {
        case LiteralInline lit:
            AddSpan(text, lit.Content.ToString(), style);
            break;

        case EmphasisInline em:
            var s = style;
            if (em.DelimiterChar is '*' or '_')
            {
                if (em.DelimiterCount >= 2) s = s with { Bold = true };
                if (em.DelimiterCount == 1 || em.DelimiterCount == 3) s = s with { Italic = true };
            }
            foreach (var child in em) RenderInlineNode(text, child, s);
            break;

        case CodeInline code:
            AddSpan(text, code.Content, style with { Mono = true });
            break;

        case LineBreakInline:
            AddSpan(text, " ", style);   // soft break -> single space
            break;

        case AutolinkInline auto:
            AddSpan(text, auto.Url, style with { Link = true });
            break;

        case LinkInline link when !link.IsImage:
            var ls = style with { Link = true };
            bool any = false;
            foreach (var child in link) { RenderInlineNode(text, child, ls); any = true; }
            if (!any && link.Url != null) AddSpan(text, link.Url, ls);
            break;

        default:
            if (node is ContainerInline ci)
                foreach (var child in ci) RenderInlineNode(text, child, style);
            else
                AddSpan(text, node.ToString() ?? "", style);
            break;
    }
}

static void AddSpan(TextDescriptor text, string s, InlineStyle style)
{
    if (string.IsNullOrEmpty(s)) return;
    var span = text.Span(s);
    if (style.Bold) span.Bold();
    if (style.Italic) span.Italic();
    if (style.Mono) span.FontFamily("Consolas").FontSize(10);
    if (style.Link) span.FontColor(Colors.Blue.Medium);
}

// ---- Images ------------------------------------------------------------------

static void RenderParagraph(ColumnDescriptor col, ParagraphBlock p)
{
    if (HasImage(p.Inline)) RenderParagraphWithImages(col, p.Inline);
    else col.Item().PaddingTop(6).Text(t => RenderInline(t, p.Inline, default));
}

static bool HasImage(ContainerInline? inline)
{
    if (inline == null) return false;
    foreach (var node in inline)
    {
        if (node is LinkInline { IsImage: true }) return true;
        if (node is ContainerInline c && HasImage(c)) return true;
    }
    return false;
}

// Renders a paragraph that mixes text and images: text runs become text items and each image a
// block image, in document order. Manual images normally sit alone in their own paragraph.
static void RenderParagraphWithImages(ColumnDescriptor col, ContainerInline? inline)
{
    if (inline == null) return;
    var pending = new List<Inline>();
    void Flush()
    {
        if (pending.Count == 0 || pending.All(n => n is LineBreakInline)) { pending.Clear(); return; }
        var nodes = pending.ToList();
        col.Item().PaddingTop(6).Text(t => { foreach (var n in nodes) RenderInlineNode(t, n, default); });
        pending.Clear();
    }
    foreach (var node in inline)
    {
        if (node is LinkInline { IsImage: true } img) { Flush(); RenderImage(col, img); }
        else pending.Add(node);
    }
    Flush();
}

static void RenderImage(ColumnDescriptor col, LinkInline img)
{
    string? url = img.Url;
    if (string.IsNullOrWhiteSpace(url)) return;

    if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        col.Item().PaddingTop(6).Text($"[remote image not embedded: {url}]").Italic().FontColor(Colors.Grey.Darken1);
        Console.Error.WriteLine($"ManualGen: skipping remote image {url}");
        return;
    }

    string path = Path.IsPathRooted(url) ? url : Path.GetFullPath(Path.Combine(ManualContext.ImageBaseDir, url));
    if (!File.Exists(path))
    {
        col.Item().PaddingTop(6).Text($"[missing image: {url}]").Italic().FontColor(Colors.Red.Medium);
        Console.Error.WriteLine($"ManualGen: image not found: {path}");
        return;
    }

    // Cap at the image's natural size (px @ 96 DPI -> points) so small screenshots are not
    // upscaled; the column already caps the width to the page, so large images scale down.
    var item = col.Item().PaddingTop(8);
    var size = ImageSize(path);
    if (size is { } sz && sz.Width > 0) item = item.MaxWidth(sz.Width * 0.75f);
    item.Image(path).FitWidth();

    string alt = ImageAltText(img);
    if (!string.IsNullOrWhiteSpace(alt))
        col.Item().PaddingTop(2).Text(alt).FontSize(9).Italic().FontColor(Colors.Grey.Darken1);
}

static string ImageAltText(LinkInline img)
{
    var sb = new System.Text.StringBuilder();
    foreach (var child in img)
        if (child is LiteralInline lit) sb.Append(lit.Content.ToString());
    return sb.ToString().Trim();
}

// Reads pixel dimensions from a PNG / GIF / JPEG header without decoding the pixels.
static (int Width, int Height)? ImageSize(string path)
{
    try
    {
        using var fs = File.OpenRead(path);
        var h = new byte[24];
        if (fs.Read(h, 0, 24) < 24) return null;

        if (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47)   // PNG (IHDR)
            return ((h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19],
                    (h[20] << 24) | (h[21] << 16) | (h[22] << 8) | h[23]);

        if (h[0] == (byte)'G' && h[1] == (byte)'I' && h[2] == (byte)'F')    // GIF
            return (h[6] | (h[7] << 8), h[8] | (h[9] << 8));

        if (h[0] == 0xFF && h[1] == 0xD8)                                   // JPEG
        {
            fs.Position = 2;
            return JpegSize(fs);
        }
    }
    catch { /* unknown / unreadable header -> fall back to fit-width */ }
    return null;
}

static (int Width, int Height)? JpegSize(Stream s)
{
    var b = new byte[5];
    while (s.Position < s.Length)
    {
        if (s.ReadByte() != 0xFF) continue;
        int marker = s.ReadByte();
        while (marker == 0xFF) marker = s.ReadByte();                       // skip fill bytes
        if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7) continue;  // markers with no payload
        if (s.Read(b, 0, 2) < 2) return null;
        int len = (b[0] << 8) | b[1];
        // Start-of-frame markers (except DHT/JPG/DAC) carry the frame dimensions.
        if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
        {
            if (s.Read(b, 0, 5) < 5) return null;
            return ((b[3] << 8) | b[4], (b[1] << 8) | b[2]);
        }
        s.Position += len - 2;                                             // skip this segment
    }
    return null;
}

static class ManualContext
{
    public static string ImageBaseDir = "";
}

readonly record struct InlineStyle(bool Bold, bool Italic, bool Mono, bool Link);
