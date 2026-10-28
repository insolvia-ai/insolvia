using System.Globalization;
using System.Text.RegularExpressions;
using Insolvia.Core.Debtors;
using Insolvia.Engines.Projections;
using Insolvia.Forms.Pdf;
using PdfSharp.Pdf;

namespace Insolvia.Forms;

/// <summary>
/// The amendment cover sheet (from <c>core/amendment_cover_sheet.py</c>,
/// issue #370) — a GENERATED informational page, never an official form: the
/// caption, the filing date, the forms this packet amends, and a note that
/// some districts want their own local cover sheet. One Letter page, drawn
/// with <see cref="PdfDraw"/>'s primitives.
/// </summary>
public static partial class AmendmentCoverSheet
{
    private const double PageWidth = 612.0;
    private const double PageHeight = 792.0;
    private const double Margin = 72.0;
    private const double TitleSize = 16.0;
    private const double BodySize = 11.0;
    private const double NoteSize = 9.0;
    private const double LineGap = 16.0;
    private const double Ink = 0.0;
    private const double Muted = 0.35;
    private const double AverageAdvance = 0.55;

    /// <summary>
    /// One page: the debtors, the district, the filing date the case
    /// carries, the releases this packet amends (in zip order), and the
    /// generated-at line. Deterministic for the same inputs.
    /// </summary>
    public static byte[] Render(CaseFile caseFile, IReadOnlyList<FormRelease> amendedReleases, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        ArgumentNullException.ThrowIfNull(amendedReleases);
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.MediaBox = new PdfRectangle(new PdfSharp.Drawing.XPoint(0, 0), new PdfSharp.Drawing.XPoint(PageWidth, PageHeight));
        var font = PdfDraw.AddHelvetica(doc);

        var ops = new List<byte>();
        var y = PageHeight - Margin;

        void Line(string text, double size, double gray, double indent = 0.0)
        {
            foreach (var part in Wrapped(text, size, indent))
            {
                ops.AddRange(PdfDraw.TextOps(part, Margin + indent, y, size, 0.0, gray));
                y -= Math.Max(LineGap, size * 1.3);
            }
        }

        void Gap(double lines = 0.5) => y -= LineGap * lines;

        Line("AMENDMENT COVER SHEET", TitleSize, Ink);
        Gap();
        Line(
            "This is a generated informational page, not an official bankruptcy form. Some districts require their own local amendment cover sheet; check this case's district's local rules separately.",
            NoteSize,
            Muted);
        Gap();

        foreach (var caption in DebtorCaption(caseFile))
        {
            Line(caption, BodySize, Ink);
        }

        if (caseFile.Case.District is { Length: > 0 } district)
        {
            Line($"District: {district}", BodySize, Ink);
        }

        var filingDate = caseFile.Case.FiledAt is { } filed ? ProjectionHelpers.FormatDate(filed) : "Not yet recorded";
        Line($"Filing date: {filingDate}", BodySize, Ink);
        Gap();

        Line("This packet amends the following:", BodySize, Ink);
        foreach (var release in amendedReleases)
        {
            Line($"- {ReleaseLabel(release)}", BodySize, Ink, indent: 12);
        }

        ops.AddRange(PdfDraw.TextOps(
            "Generated " + generatedAt.DateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC",
            Margin,
            Margin,
            NoteSize,
            0.0,
            Muted));

        PdfDraw.DrawOnPage(doc, page, font, [.. ops]);
        return Determinism.Save(doc, [.. ops], created: true);
    }

    private static List<string> DebtorCaption(CaseFile caseFile)
    {
        var lines = new List<string>();
        if (ProjectionHelpers.FullName(caseFile.Debtor(FilingRole.Debtor1)) is { Length: > 0 } first)
        {
            lines.Add($"Debtor 1: {first}");
        }

        if (ProjectionHelpers.FullName(caseFile.Debtor(FilingRole.Debtor2)) is { Length: > 0 } second)
        {
            lines.Add($"Debtor 2: {second}");
        }

        return lines;
    }

    private static string ReleaseLabel(FormRelease release) =>
        release.OfficialNumber.Length > 0
            ? $"{release.OfficialNumber} - {release.Title}"
            : release.Title.Length > 0 ? release.Title : release.Form;

    private static List<string> Wrapped(string text, double size, double indent)
    {
        var usable = PageWidth - (2 * Margin) - indent;
        return TextWrap.Wrap(text, Math.Max(20, (int)(usable / (size * AverageAdvance))));
    }
}

/// <summary>
/// CPython's <c>textwrap.wrap</c> with its defaults: whitespace collapsed to
/// spaces, words split on whitespace and after hyphens, long words broken,
/// leading/trailing whitespace dropped per line.
/// </summary>
internal static partial class TextWrap
{
    private const string Whitespace = "\\t\\n\\x0b\\x0c\\r ";

    // textwrap.TextWrapper.wordsep_re, verbatim in .NET syntax.
    [GeneratedRegex(
        @"([" + Whitespace + @"]+"
        + @"|(?<=[\w!""'&.,?])-{2,}(?=\w)"
        + @"|[^" + Whitespace + @"]+?(?:-(?:(?<=[^\d\W]{2}-)|(?<=[^\d\W]-[^\d\W]-))(?=[^\d\W]-?[^\d\W])|(?=[" + Whitespace + @"]|\z)|(?<=[\w!""'&.,?])(?=-{2,}\w)))")]
    private static partial Regex WordSeparator();

    public static List<string> Wrap(string text, int width)
    {
        // _munge_whitespace: expand tabs, then every whitespace char to a space.
        var munged = new string([.. text.Replace("\t", "        ", StringComparison.Ordinal).Select(c => c is '\n' or '\x0b' or '\x0c' or '\r' ? ' ' : c)]);
        var chunks = WordSeparator().Split(munged).Where(chunk => chunk.Length > 0).ToList();
        chunks.Reverse();
        var lines = new List<string>();
        while (chunks.Count > 0)
        {
            var line = new List<string>();
            var length = 0;
            if (chunks[^1].Trim().Length == 0 && lines.Count > 0)
            {
                chunks.RemoveAt(chunks.Count - 1);
            }

            while (chunks.Count > 0)
            {
                var size = chunks[^1].Length;
                if (length + size > width)
                {
                    break;
                }

                line.Add(chunks[^1]);
                chunks.RemoveAt(chunks.Count - 1);
                length += size;
            }

            if (chunks.Count > 0 && chunks[^1].Length > width)
            {
                var spaceLeft = width < 1 ? 1 : width - length;
                var chunk = chunks[^1];
                var end = spaceLeft;
                if (chunk.Length > spaceLeft)
                {
                    var hyphen = chunk.LastIndexOf('-', Math.Max(0, spaceLeft - 1));
                    if (spaceLeft > 0 && hyphen > 0 && chunk[..hyphen].Any(c => c != '-'))
                    {
                        end = hyphen + 1;
                    }
                }

                line.Add(chunk[..end]);
                chunks[^1] = chunk[end..];
            }

            if (line.Count > 0 && line[^1].Trim().Length == 0)
            {
                line.RemoveAt(line.Count - 1);
            }

            if (line.Count > 0)
            {
                lines.Add(string.Concat(line));
            }
        }

        return lines;
    }
}
