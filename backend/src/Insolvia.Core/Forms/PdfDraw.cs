using System.Text;
using PdfSharp.Pdf;
using static Insolvia.Forms.Pdf.PdfObjects;

namespace Insolvia.Forms;

/// <summary>
/// Raw content-stream text drawing (from <c>core/pdf_draw.py</c>), shared by
/// every pass that marks up an already-rendered page: the "Draft" watermark
/// and print-date stamp, and the amendment cover sheet. A standard-14
/// Helvetica, nothing embedded.
/// </summary>
public static class PdfDraw
{
    /// <summary>The resource name every drawn font is registered under.</summary>
    public const string OverlayFontResource = "/InsolviaOverlay";

    /// <summary>Escape a literal string for a content stream: backslash first.</summary>
    public static string PdfEscape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
    }

    /// <summary>
    /// The operators for one line of text: a rotation about (x, y), a gray
    /// fill, and a <c>Tj</c> — in cp1252, a character outside it printing as
    /// "?" rather than failing a whole packet over a caption.
    /// </summary>
    public static byte[] TextOps(string text, double x, double y, double size, double rotateDegrees, double gray)
    {
        var radians = rotateDegrees * Math.PI / 180.0;
        double a = Math.Cos(radians), b = Math.Sin(radians), c = -Math.Sin(radians), d = Math.Cos(radians);
        var ops =
            "q\n"
            + $"{F(a, 6)} {F(b, 6)} {F(c, 6)} {F(d, 6)} {F(x, 2)} {F(y, 2)} cm\n"
            + $"{F(gray, 2)} g\n"
            + "BT\n"
            + $"{OverlayFontResource} {F(size, 1)} Tf\n"
            + "0 0 Td\n"
            + $"({PdfEscape(text)}) Tj\n"
            + "ET\n"
            + "Q\n";
        return ReplacingCp1252.GetBytes(ops);
    }

    private static string F(double value, int digits) => FormFill.FixedPoint(value, digits);

    /// <summary>cp1252 with Python's <c>errors="replace"</c>: one "?" per unencodable code point.</summary>
    private static readonly Encoding ReplacingCp1252 = MakeReplacing();

    private static Encoding MakeReplacing()
    {
        _ = FormFill.Cp1252; // registers the code-pages provider
        return Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
    }

    /// <summary>A standard-14 Helvetica in WinAnsiEncoding, as a (direct) font dictionary.</summary>
    internal static PdfDictionary HelveticaDictionary(PdfDocument doc)
    {
        var font = new PdfDictionary(doc);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/Helvetica");
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        return font;
    }

    /// <summary>Register Helvetica as an indirect object (pypdf's <c>add_helvetica</c>).</summary>
    internal static PdfDictionary AddHelvetica(PdfDocument doc)
    {
        var font = HelveticaDictionary(doc);
        doc.Internals.AddObject(font);
        return font;
    }

    /// <summary>
    /// Append <paramref name="ops"/> to the page's content, registering the
    /// font as <see cref="OverlayFontResource"/>: the existing content (an
    /// array's parts joined, each ending in a newline) is kept, a newline,
    /// then the new operators — one replacement stream.
    /// </summary>
    internal static void DrawOnPage(PdfDocument doc, PdfPage page, PdfDictionary font, byte[] ops)
    {
        if (Dict(page.Elements["/Resources"]) is not { } resources)
        {
            resources = new PdfDictionary(doc);
            page.Elements["/Resources"] = resources;
        }

        if (Dict(resources.Elements["/Font"]) is not { } fonts)
        {
            fonts = new PdfDictionary(doc);
            resources.Elements["/Font"] = fonts;
        }

        fonts.Elements[OverlayFontResource] = font.Reference;
        var existing = ConcatenatedContents(page);
        byte[] combined = existing is null ? ops : [.. existing, (byte)'\n', .. ops];
        var stream = new PdfDictionary(doc);
        stream.CreateStream(combined);
        doc.Internals.AddObject(stream);
        page.Elements["/Contents"] = stream.Reference;
    }

    /// <summary>pypdf's <c>ContentStream</c> over /Contents: an array's streams decoded and joined, each followed by a newline unless it already ends in one.</summary>
    internal static byte[]? ConcatenatedContents(PdfPage page)
    {
        var item = Resolve(page.Elements["/Contents"]);
        if (item is PdfDictionary single)
        {
            return Decoded(single);
        }

        if (item is not PdfArray array)
        {
            return null;
        }

        var data = new List<byte>();
        foreach (var part in array.Elements)
        {
            if (Dict(part) is { } stream)
            {
                data.AddRange(Decoded(stream));
                if (data.Count == 0 || data[^1] != (byte)'\n')
                {
                    data.Add((byte)'\n');
                }
            }
        }

        return [.. data];
    }
}
