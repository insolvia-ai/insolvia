using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Insolvia.Core.Documents;

// One document of a case — METADATA only; the bytes are in the bucket (issue
// 8.6; from documents.py). A document is provenance's OBJECT, never its
// subject, so it carries no provenance map.

/// <summary>What the object store reports about an object that exists — the FACTS, as against a pending record's claims.</summary>
public sealed record StoredBlob(long ByteSize, string Etag);

/// <summary>
/// One document of a case. <see cref="ByteSize"/> changes meaning with
/// <see cref="Status"/>: claimed while pending, counted by the store once stored.
/// </summary>
/// <param name="Id">The server-minted uuid.</param>
/// <param name="CaseId">The case.</param>
/// <param name="Kind">What it is.</param>
/// <param name="FileName">The uploader's file name — metadata, never part of a key.</param>
/// <param name="ContentType">The validated content type, bound into the upload capability.</param>
/// <param name="ByteSize">Claimed, then counted.</param>
/// <param name="StorageRef">Where the bytes live: <c>cases/&lt;case&gt;/&lt;document&gt;</c>.</param>
/// <param name="UploadedBy">Who was authorised to upload it.</param>
/// <param name="UploadedAt">When the upload was AUTHORISED.</param>
public sealed record Document(
    string Id,
    string CaseId,
    DocumentKind Kind,
    string FileName,
    DocumentContentType ContentType,
    long ByteSize,
    string StorageRef,
    string UploadedBy,
    DateTimeOffset UploadedAt)
{
    /// <summary>Whether the bytes are known to have arrived.</summary>
    public DocumentStatus Status { get; init; } = DocumentStatus.Pending;

    /// <summary>What the store reported identifies the bytes, once stored.</summary>
    public string? Etag { get; init; }

    /// <summary>Staff, or the client through the portal.</summary>
    public DocumentChannel Channel { get; init; } = DocumentChannel.Staff;

    /// <summary>The document request the upload answers.</summary>
    public string? RequestId { get; init; }
}

/// <summary>A validated upload request, before server-generated identity.</summary>
public sealed record DocumentDraft(DocumentKind Kind, string FileName, DocumentContentType ContentType, long ByteSize);

/// <summary>The document rules.</summary>
public static partial class DocumentRules
{
    /// <summary>The largest upload: 50 MB.</summary>
    public const long MaxByteSize = 50 * 1024 * 1024;

    /// <summary>The cap on a file name.</summary>
    public const int MaxFileName = 255;

    /// <summary>The tag every presigned PUT writes; the lifecycle rule reaps tagged objects after a day.</summary>
    public const string UploadTag = "upload=unconfirmed";

    /// <summary>
    /// When a capability minted now stops working — returned to the client so it
    /// can tell an expired URL from a refused one.
    /// </summary>
    public static DateTimeOffset ExpiryTimestamp(int seconds, TimeProvider? clock = null) =>
        Clock.NowMillis(clock).AddSeconds(seconds);

    /// <summary>
    /// Validate POST /v1/cases/&lt;case_id&gt;/documents; unknown keys are ignored.
    /// <paramref name="kind"/>, when given, is the kind the SERVER decided (a
    /// client upload takes its request's kind), and the payload's is ignored.
    /// Every check runs BEFORE an upload URL exists.
    /// </summary>
    public static DocumentDraft ParseUpload(JsonObject payload, DocumentKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload["provenance"] is not null)
        {
            errors["provenance"] = "A document record carries no provenance. Provenance describes values extracted FROM a document, not the document itself.";
        }

        var parsedKind = kind ?? ParseKind(payload["kind"], errors);
        var fileName = ParseFileName(payload["fileName"], errors);
        var contentType = ParseContentType(payload["contentType"], errors);
        var byteSize = ParseByteSize(payload["byteSize"], errors);
        if (errors.Count > 0 || parsedKind is null || fileName is null || contentType is null || byteSize is null)
        {
            throw new FieldValidationException(errors);
        }

        return new DocumentDraft(parsedKind.Value, fileName, contentType.Value, byteSize.Value);
    }

    /// <summary>
    /// Where the bytes live. NO PII IN AN OBJECT KEY, EVER: both halves are
    /// checked against the uuid pattern rather than sanitised, so no path
    /// produces a key holding anything a human typed.
    /// </summary>
    public static string ObjectKey(string caseId, string documentId)
    {
        ArgumentNullException.ThrowIfNull(caseId);
        ArgumentNullException.ThrowIfNull(documentId);
        if (!Uuid().IsMatch(caseId) || !Uuid().IsMatch(documentId))
        {
            throw new ValidationException("object keys are built from server-minted uuids only");
        }

        return $"cases/{caseId}/{documentId}";
    }

    /// <summary>
    /// Stamp a draft with identity and its storage location. The channel is the
    /// route's to say; the request id one the route resolved in this case.
    /// </summary>
    public static Document Create(
        DocumentDraft draft,
        string caseId,
        string uploadedBy,
        DocumentChannel channel = DocumentChannel.Staff,
        string? requestId = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var documentId = Guid.NewGuid().ToString();
        return new Document(documentId, caseId, draft.Kind, draft.FileName, draft.ContentType, draft.ByteSize, ObjectKey(caseId, documentId), uploadedBy, Clock.NowMillis(clock))
        {
            Channel = channel,
            RequestId = requestId,
        };
    }

    /// <summary>
    /// The record moved to stored, recording what the store reports. THE SIZE
    /// IS REPLACED, NOT COMPARED. Idempotent: a retried confirm gets the same row.
    /// </summary>
    public static Document Confirm(Document document, StoredBlob blob)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(blob);
        return document with { Status = DocumentStatus.Stored, ByteSize = blob.ByteSize, Etag = blob.Etag };
    }

    /// <summary>Newest first, tie-broken by id (descending) — the ONE definition both stores sort by.</summary>
    public static IComparer<Document> ListOrder { get; } = Comparer<Document>.Create((left, right) =>
    {
        var byTime = right.UploadedAt.CompareTo(left.UploadedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(right.Id, left.Id);
    });

    private static DocumentKind? ParseKind(JsonNode? value, Dictionary<string, string> errors)
    {
        if (!Wire.TryParse<DocumentKind>(Json.AsString(value), out var kind))
        {
            errors["kind"] = "Kind must be one of " + string.Join(", ", Wire.Names<DocumentKind>()) + ".";
            return null;
        }

        return kind;
    }

    private static DocumentContentType? ParseContentType(JsonNode? value, Dictionary<string, string> errors)
    {
        var raw = Json.AsString(value);
        if (raw is null)
        {
            errors["contentType"] = "A content type is required.";
            return null;
        }

        if (!Wire.TryParse<DocumentContentType>(TextRules.Lower(TextRules.Strip(raw)), out var contentType))
        {
            errors["contentType"] = "That file type is not accepted. Allowed: " + string.Join(", ", Wire.Names<DocumentContentType>()) + ".";
            return null;
        }

        return contentType;
    }

    private static long? ParseByteSize(JsonNode? value, Dictionary<string, string> errors)
    {
        if (!Json.TryGetWholeNumber(value, out var size))
        {
            errors["byteSize"] = "A size in bytes is required.";
            return null;
        }

        if (size < 1)
        {
            errors["byteSize"] = "A document must have at least one byte.";
            return null;
        }

        if (size > MaxByteSize)
        {
            errors["byteSize"] = $"A document must be {MaxByteSize / (1024 * 1024)} MB or smaller.";
            return null;
        }

        return size;
    }

    private static string? ParseFileName(JsonNode? value, Dictionary<string, string> errors)
    {
        var raw = Json.AsString(value);
        if (raw is null || TextRules.Strip(raw).Length == 0)
        {
            errors["fileName"] = "A file name is required.";
            return null;
        }

        var fileName = TextRules.Strip(raw);
        if (TextRules.Length(fileName) > MaxFileName)
        {
            errors["fileName"] = $"Must be {MaxFileName} characters or fewer.";
            return null;
        }

        if (FileNameForbidden().IsMatch(fileName))
        {
            errors["fileName"] = "Must not contain path separators, control characters, or invisible or direction-changing characters.";
            return null;
        }

        if (fileName is "." or "..")
        {
            errors["fileName"] = "Must be a file name.";
            return null;
        }

        return fileName;
    }

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex Uuid();

    // Path separators; C0 controls and DEL; the soft hyphen; zero-width and
    // direction marks; bidi embeddings, overrides and isolates; the word
    // joiner; the BOM.
    [GeneratedRegex("[/\\\\\u0000-\u001f\u007f­​-‏‪-‮⁠⁦-⁩﻿]")]
    private static partial Regex FileNameForbidden();
}

/// <summary>The file types an upload may be (Python's <c>CONTENT_TYPES</c>).</summary>
public enum DocumentContentType
{
    /// <summary>The wire value <c>application/pdf</c>.</summary>
    [JsonStringEnumMemberName("application/pdf")]
    Pdf,

    /// <summary>The wire value <c>image/jpeg</c>.</summary>
    [JsonStringEnumMemberName("image/jpeg")]
    Jpeg,

    /// <summary>The wire value <c>image/png</c>.</summary>
    [JsonStringEnumMemberName("image/png")]
    Png,

    /// <summary>The wire value <c>image/heic</c>.</summary>
    [JsonStringEnumMemberName("image/heic")]
    Heic,

    /// <summary>The wire value <c>image/tiff</c>.</summary>
    [JsonStringEnumMemberName("image/tiff")]
    Tiff,
}

/// <summary>Whether the bytes are known to have arrived (Python's <c>STATUSES</c>).</summary>
public enum DocumentStatus
{
    /// <summary>An upload was authorised; nothing confirmed it landed.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>The store reported the object; the record holds its facts.</summary>
    [JsonStringEnumMemberName("stored")]
    Stored,
}

/// <summary>Who uploaded (Python's <c>CHANNELS</c>).</summary>
public enum DocumentChannel
{
    /// <summary>The firm's staff.</summary>
    [JsonStringEnumMemberName("staff")]
    Staff,

    /// <summary>The client, through the portal (ADR 0023).</summary>
    [JsonStringEnumMemberName("client")]
    Client,
}
