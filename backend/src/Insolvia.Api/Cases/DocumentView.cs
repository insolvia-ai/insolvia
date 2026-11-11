using System.Text.Json.Serialization;
using Insolvia.Domain.Documents;

namespace Insolvia.Api.Cases;

/// <summary>
/// A document as staff read it: its metadata, never its bytes. The uploader,
/// the storage key and the etag stay on the server.
/// </summary>
/// <param name="Id">The document's id.</param>
/// <param name="CaseId">The case.</param>
/// <param name="Kind">What the document is.</param>
/// <param name="FileName">The uploader's file name.</param>
/// <param name="ContentType">The content type the upload was bound to.</param>
/// <param name="ByteSize">The size claimed while pending, the size the bucket counted once stored.</param>
/// <param name="UploadedAt">When the upload was authorised, ISO 8601 UTC to the millisecond.</param>
/// <param name="Status">Whether the bytes have arrived.</param>
/// <param name="Channel">Staff, or the client through the portal.</param>
/// <param name="RequestId">The document request the upload answers; null when none.</param>
public sealed record DocumentView(
    string Id,
    string CaseId,
    DocumentKind Kind,
    string FileName,
    DocumentContentType ContentType,
    long ByteSize,
    string UploadedAt,
    DocumentStatus Status,
    DocumentChannel Channel,
    string? RequestId)
{
    /// <summary>The view of <paramref name="document"/>.</summary>
    /// <param name="document">The stored document.</param>
    /// <returns>The view.</returns>
    public static DocumentView Of(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(document.Id, document.CaseId, document.Kind, document.FileName, document.ContentType, document.ByteSize, WireFormat.Millis(document.UploadedAt), document.Status, document.Channel, document.RequestId);
    }
}

/// <summary>A client's view of their own upload, through the portal: no case id, no channel.</summary>
/// <param name="Id">The document's id.</param>
/// <param name="RequestId">The document request the upload answers; null when none.</param>
/// <param name="FileName">The client's file name.</param>
/// <param name="ContentType">The content type the upload was bound to.</param>
/// <param name="ByteSize">The size claimed while pending, the size the bucket counted once stored.</param>
/// <param name="UploadedAt">When the upload was authorised, ISO 8601 UTC to the millisecond.</param>
/// <param name="Status">Whether the bytes have arrived.</param>
public sealed record PortalDocumentView(
    string Id,
    string? RequestId,
    string FileName,
    DocumentContentType ContentType,
    long ByteSize,
    string UploadedAt,
    DocumentStatus Status)
{
    /// <summary>The client's view of <paramref name="document"/>.</summary>
    /// <param name="document">The stored document.</param>
    /// <returns>The view.</returns>
    public static PortalDocumentView Of(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(document.Id, document.RequestId, document.FileName, document.ContentType, document.ByteSize, WireFormat.Millis(document.UploadedAt), document.Status);
    }
}

/// <summary>A case's documents.</summary>
/// <param name="Documents">The documents.</param>
public sealed record DocumentList(IReadOnlyList<DocumentView> Documents);

/// <summary>One document.</summary>
/// <param name="Document">The document.</param>
public sealed record DocumentResult(DocumentView Document);

/// <summary>A new document and where to put its bytes.</summary>
/// <param name="Document">The document, pending until <c>…/complete</c>.</param>
/// <param name="Upload">The presigned PUT the bytes go to.</param>
public sealed record DocumentUploadTicket(DocumentView Document, PresignedRequest Upload);

/// <summary>
/// A short-lived presigned request straight to the bucket: the bytes never
/// pass through the API.
/// </summary>
/// <param name="Url">The presigned URL.</param>
/// <param name="Method"><c>PUT</c> for an upload, <c>GET</c> for a download.</param>
/// <param name="Headers">For an upload, the exact headers it was signed for — the bucket refuses anything else; absent for a download.</param>
/// <param name="ExpiresAt">When the URL stops working, ISO 8601 UTC to the millisecond — so a client can tell an expired URL from a refused one.</param>
public sealed record PresignedRequest(
    string Url,
    string Method,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UploadHeaders? Headers,
    string ExpiresAt);

/// <summary>The headers a presigned upload was signed for; send exactly these.</summary>
/// <param name="ContentType">The document's content type.</param>
/// <param name="Tagging">The unconfirmed-upload tag; the bucket reaps a tagged object after a day.</param>
public sealed record UploadHeaders(
    [property: JsonPropertyName("Content-Type")] string ContentType,
    [property: JsonPropertyName("x-amz-tagging")] string Tagging);

/// <summary>
/// A new upload (<c>POST …/documents</c>). Parsed by <c>DocumentRules.ParseUpload</c>,
/// which owns validation; this record is its shape for the OpenAPI document.
/// </summary>
/// <param name="Kind">What the document is.</param>
/// <param name="FileName">The file name, at most 255 characters.</param>
/// <param name="ContentType">The content type the upload will carry.</param>
/// <param name="ByteSize">The size in bytes, at most 50 MB.</param>
/// <param name="RequestId">The document request it answers, in this case and still open; absent when none.</param>
public sealed record DocumentUploadRequest(
    DocumentKind Kind,
    string FileName,
    DocumentContentType ContentType,
    long ByteSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestId);

/// <summary>A client's new upload: the document, and where to PUT its bytes.</summary>
/// <param name="Document">The document, as the client sees it.</param>
/// <param name="Upload">The presigned PUT.</param>
public sealed record PortalUploadTicket(PortalDocumentView Document, PresignedRequest Upload);

/// <summary>A client's upload, completed.</summary>
/// <param name="Document">The document, as the client sees it.</param>
public sealed record PortalUpload(PortalDocumentView Document);
