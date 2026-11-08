using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static Insolvia.Forms.Pdf.PdfObjects;

namespace Insolvia.Forms;

/// <summary>
/// What a packet's files measure (from <c>core/pdf_measure.py</c>) — the
/// facts the filing set's PDF checks judge: size, pages with no text layer,
/// and pages that are not US letter. Measured once, at assembly. Only counts
/// and sizes leave here; a page's text is read only to decide whether it
/// HAS any (B121's text is the debtor's full SSN).
/// </summary>
public static partial class PdfMeasure
{
    /// <summary>US letter in points, either orientation.</summary>
    public static readonly (double Short, double Long) LetterPoints = (612.0, 792.0);

    private const double LetterTolerancePoints = 1.5;

    [GeneratedRegex(@"(?:\bT[jJ]\b|[)\]]\s*['""])", RegexOptions.ECMAScript)]
    private static partial Regex TextOperator();

    /// <summary>
    /// One zip entry's measurements, with the SHA-256 of its bytes. A
    /// <c>.pdf</c> has every page measured; anything else is a size only. A
    /// PDF that cannot be parsed measures as zero pages, unreadable.
    /// </summary>
    public static PacketPart MeasurePart(string name, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(content);
        var digest = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!name.ToLowerInvariant().EndsWith(".pdf", StringComparison.Ordinal))
        {
            return new PacketPart(name, content.Length, Sha256: digest);
        }

        try
        {
            using var source = new MemoryStream(content, writable: false);
            using var doc = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            var pages = doc.Pages.Cast<PdfPage>().ToList();
            return new PacketPart(
                name,
                content.Length,
                pages.Count,
                pages.Count(page => !IsLetter(page)),
                pages.Count(page => !HasText(page)),
                Sha256: digest);
        }
#pragma warning disable CA1031 // pypdf's PyPdfError: ANY failure to parse is "unreadable", the measured fact
        catch (Exception)
#pragma warning restore CA1031
        {
            return new PacketPart(name, content.Length, 0, 0, 0, Unreadable: true, Sha256: digest);
        }
    }

    /// <summary>Every part, in the packet's own (zip) order.</summary>
    public static IReadOnlyList<PacketPart> MeasureParts(IReadOnlyList<(string Name, byte[] Content)> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return [.. parts.Select(part => MeasurePart(part.Name, part.Content))];
    }

    private static bool IsLetter(PdfPage page)
    {
        var (width, height) = MediaBoxSize(page);
        var sides = new[] { Math.Abs(width), Math.Abs(height) }.Order().ToArray();
        return Math.Abs(sides[0] - LetterPoints.Short) <= LetterTolerancePoints
            && Math.Abs(sides[1] - LetterPoints.Long) <= LetterTolerancePoints;
    }

    /// <summary>
    /// A text-showing operator in the page's own content, or — pypdf's
    /// <c>extract_text</c> fallback — in a form XObject the page invokes.
    /// </summary>
    private static bool HasText(PdfPage page)
    {
        if (PdfDraw.ConcatenatedContents(page) is { } contents && TextOperator().IsMatch(System.Text.Encoding.Latin1.GetString(contents)))
        {
            return true;
        }

        return XObjectsHaveText(Dict(page.Elements["/Resources"]), new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), 0);
    }

    private static bool XObjectsHaveText(PdfDictionary? resources, HashSet<PdfDictionary> seen, int depth)
    {
        if (depth > 8 || Dict(resources?.Elements["/XObject"]) is not { } xobjects)
        {
            return false;
        }

        foreach (var key in xobjects.Elements.Keys)
        {
            if (Dict(xobjects.Elements[key]) is not { } xobject || !seen.Add(xobject) || Str(xobject, "/Subtype") != "/Form")
            {
                continue;
            }

            var text = System.Text.Encoding.Latin1.GetString(Decoded(xobject));
            if (TextOperator().IsMatch(text) || XObjectsHaveText(Dict(xobject.Elements["/Resources"]), seen, depth + 1))
            {
                return true;
            }
        }

        return false;
    }
}
