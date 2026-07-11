using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Markdown;

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
// Reflow soft-wrapped paragraphs (QuestPDF.Markdown drops the space at a soft line break), and
// swap the menu-path arrow for a font-safe character (Lato has no U+25B8 glyph).
string markdown = Reflow(File.ReadAllText(input)).Replace("▸", ">");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

Document.Create(container =>
{
    container.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontSize(11));

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

            col.Item().PaddingTop(16).Markdown(markdown);
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

// Joins soft-wrapped prose/list lines into one line per paragraph so the renderer inserts proper
// spacing. Blank lines, headings, list-item starts, blockquotes, tables, rules and fenced code
// keep their own lines.
static string Reflow(string md)
{
    var lines = md.Replace("\r\n", "\n").Split('\n');
    var sb = new StringBuilder();
    string current = "";
    bool inFence = false;

    void Flush()
    {
        if (current.Length > 0) { sb.Append(current).Append('\n'); current = ""; }
    }

    foreach (var line in lines)
    {
        string trimmed = line.TrimStart();

        if (trimmed.StartsWith("```"))
        {
            Flush();
            sb.Append(line).Append('\n');
            inFence = !inFence;
            continue;
        }
        if (inFence) { sb.Append(line).Append('\n'); continue; }

        if (line.Trim().Length == 0) { Flush(); sb.Append('\n'); continue; }

        if (IsBlockStart(trimmed))
        {
            Flush();
            current = line.TrimEnd();
        }
        else if (current.Length == 0)
        {
            current = line.Trim();
        }
        else
        {
            current += " " + line.Trim();   // continuation of the current paragraph / list item
        }
    }
    Flush();
    return sb.ToString();
}

// True when a line begins a new block that must stay on its own line.
static bool IsBlockStart(string t)
{
    if (t.Length == 0) return false;
    char c = t[0];
    if (c is '#' or '>' or '|') return true;                        // heading, blockquote, table row
    if (t.StartsWith("---") || t.StartsWith("***") || t.StartsWith("___")) return true; // rule
    if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("+ ")) return true;    // bullet
    int i = 0;
    while (i < t.Length && char.IsDigit(t[i])) i++;                 // ordered list "1. " / "1) "
    return i > 0 && i + 1 < t.Length && (t[i] is '.' or ')') && t[i + 1] == ' ';
}
