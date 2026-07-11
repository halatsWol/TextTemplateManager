using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

// Build-time generator: renders docs/Handbook.md to a PDF next to the app exe.
// Args: <input.md> <output.pdf> [version]
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: HandbookGen <input.md> <output.pdf> [version]");
    return 1;
}

string input = args[0];
string output = args[1];
string version = args.Length >= 3 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : "0.0.0-dev";
if (!File.Exists(input))
{
    Console.Error.WriteLine($"HandbookGen: input not found: {input}");
    return 1;
}

QuestPDF.Settings.License = LicenseType.Community;

// Lato (QuestPDF's bundled font) has no U+25B8 glyph — use a plain '>' for menu paths.
string raw = File.ReadAllText(input).Replace("▸", ">");
var pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();
var document = Markdown.Parse(raw, pipeline);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

Document.Create(container =>
{
    container.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontSize(11).LineHeight(1.35f));

        page.Header().Text("Text Template Manager — Handbook")
            .SemiBold().FontColor(Colors.Grey.Darken2);

        page.Content().Column(col =>
        {
            // Cover / title block (once, at the top of the first page).
            col.Item().Text("Text Template Manager").FontSize(26).Bold().FontColor(Colors.Grey.Darken4);
            col.Item().Text("User Handbook").FontSize(14).FontColor(Colors.Grey.Darken1);
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

Console.WriteLine($"HandbookGen: {output}");
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
            col.Item().PaddingTop(6).Text(t => RenderInline(t, p.Inline, default));
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
                            if (b is ParagraphBlock pb) inner.Item().Text(t => RenderInline(t, pb.Inline, default));
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

readonly record struct InlineStyle(bool Bold, bool Italic, bool Mono, bool Link);
