using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Core.Documents;

namespace Insolvia.Filing;

/// <summary>
/// What the worker keeps once the court confirmed a filing (from
/// <c>core/receipt.py</c>): the court's confirmation page VERBATIM as a blob
/// (not a case Document — HTML is outside the documents allowlist), and the
/// one-page receipt PDF (<see cref="Forms.FilingReceipt"/>) as a
/// <c>court_notice</c> Document — born stored, its id derived from the
/// filing so a redelivery finds the first run's row instead of writing a second.
/// </summary>
public static partial class Receipts
{
    /// <summary>The receipt's document kind.</summary>
    public const DocumentKind Kind = DocumentKind.CourtNotice;

    /// <summary>The receipt's file name.</summary>
    public const string FileName = "court-filing-receipt.pdf";

    /// <summary>The receipt's content type.</summary>
    public const string ReceiptContentType = "application/pdf";

    /// <summary>The verbatim page's content type.</summary>
    public const string ConfirmationContentType = "text/html; charset=utf-8";

    // A fixed random namespace: never change it, or a capture in flight
    // across the deploy would write a second receipt row.
    private static readonly Guid Namespace = Guid.Parse("5d0f5e4e-2f58-4a7e-9a0b-4f3c1f8e2b61");

    /// <summary>
    /// Where a filing's own objects live: <c>cases/&lt;case&gt;/filings/&lt;filing&gt;/&lt;id&gt;</c>
    /// — a prefix of its own, so the worker's write grant can never touch an
    /// uploaded document or a packet. Server-minted uuids only, checked, never sanitised.
    /// </summary>
    public static string FilingObjectKey(string caseId, string filingId, string objectId)
    {
        foreach (var value in new[] { caseId, filingId, objectId })
        {
            if (value is null || !Uuid().IsMatch(value))
            {
                throw new ValidationException("object keys are built from server-minted uuids only");
            }
        }

        return $"cases/{caseId}/filings/{filingId}/{objectId}";
    }

    /// <summary>Where the verbatim confirmation page is stored.</summary>
    public static string ConfirmationRef(string caseId, string filingId) => FilingObjectKey(caseId, filingId, Guid.NewGuid().ToString());

    /// <summary>Python's <c>uuid.uuid5(namespace, f"receipt:{filing_id}")</c>.</summary>
    public static string ReceiptDocumentId(string filingId) => Uuid5(Namespace, "receipt:" + filingId);

    /// <summary>The receipt's Document record: stored, at the filing's prefix, under the derived id.</summary>
    public static Document ReceiptDocument(Core.Filings.Filing filing, byte[] content, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(filing);
        ArgumentNullException.ThrowIfNull(content);
        var id = ReceiptDocumentId(filing.FilingId);
        return new Document(id, filing.CaseId, Kind, FileName, DocumentContentType.Pdf, content.Length, FilingObjectKey(filing.CaseId, filing.FilingId, id), filing.AttorneyId, Clock.NowMillis(clock))
        {
            Status = DocumentStatus.Stored,
        };
    }

    /// <summary>RFC 4122 name-based (SHA-1) uuid, as Python's <c>uuid5</c>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "RFC 4122 version 5 is defined over SHA-1; this derives an id, it protects nothing.")]
    internal static string Uuid5(Guid space, string name)
    {
        var namespaceBytes = space.ToByteArray(bigEndian: true);
        var hash = SHA1.HashData([.. namespaceBytes, .. Encoding.UTF8.GetBytes(name)]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex Uuid();
}
