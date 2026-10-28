using System.Security.Cryptography;
using System.Text;
using PdfSharp.Pdf;

namespace Insolvia.Forms.Pdf;

/// <summary>
/// Same input, same bytes. PDFsharp 6.2.4 stamps every save with a fresh
/// second /ID element, a regenerated XMP packet carrying random
/// <c>xmpMM:DocumentID</c>/<c>InstanceID</c> GUIDs, and the clock in
/// /ModDate (and /CreationDate on a new document), with no switch to turn
/// any of it off. This pins each to a value derived from the input — the
/// property the goldens' sha256 relies on.
/// </summary>
internal static class Determinism
{
    /// <summary>The one timestamp every output carries: obviously synthetic, like the zip's DOS epoch.</summary>
    public static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Save <paramref name="doc"/> deterministically; <paramref name="seed"/>
    /// (the input bytes) seeds the XMP GUIDs. A document this engine created
    /// (<paramref name="created"/>) also gets its /CreationDate and both /ID
    /// elements pinned; an opened template keeps its own.
    /// </summary>
    public static byte[] Save(PdfDocument doc, ReadOnlySpan<byte> seed, bool created = false)
    {
        doc.Info.ModificationDate = Epoch;
        var hash = SHA256.HashData(seed);
        if (created)
        {
            doc.Info.CreationDate = Epoch;
            doc.Internals.FirstDocumentID = Convert.ToHexString(hash.AsSpan(0, 16));
        }

        // The second /ID element is "this revision": derive it from the first
        // (the template's own) instead of a fresh GUID.
        doc.Internals.SecondDocumentID = doc.Internals.FirstDocumentID;
        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return PinXmpUuids(output.ToArray(), seed);
    }

    /// <summary>
    /// Overwrite each <c>uuid:</c> GUID of the (uncompressed) XMP packet in
    /// place with one derived from the input's hash — same length, so the
    /// xref offsets stay valid.
    /// </summary>
    public static byte[] PinXmpUuids(byte[] pdf, ReadOnlySpan<byte> seed)
    {
        var hash = SHA256.HashData(seed);
        var marker = "uuid:"u8;
        var n = 0;
        var i = pdf.AsSpan().IndexOf(marker);
        while (i >= 0)
        {
            var at = i + marker.Length;
            if (at + 36 <= pdf.Length && pdf[at + 8] == '-' && pdf[at + 13] == '-' && pdf[at + 18] == '-' && pdf[at + 23] == '-')
            {
                var bytes = hash.AsSpan(0, 16).ToArray();
                bytes[15] ^= (byte)n++;
                Encoding.ASCII.GetBytes(new Guid(bytes).ToString("D")).CopyTo(pdf, at);
            }

            var next = pdf.AsSpan(at).IndexOf(marker);
            i = next < 0 ? -1 : at + next;
        }

        return pdf;
    }
}
