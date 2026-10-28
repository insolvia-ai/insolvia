using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Insolvia.Core.Provenance;

/// <summary>
/// Who supplied a value. Order is Python's <c>SOURCES</c> tuple — it is the
/// order the "Source must be one of …" message lists them in.
/// </summary>
public enum ProvenanceSource
{
    /// <summary>A person at the firm typed it. Typing IS the confirmation.</summary>
    [JsonStringEnumMemberName("staff_typed")]
    StaffTyped,

    /// <summary>Read from a document by extraction. Machine-supplied: needs a person's confirmation.</summary>
    [JsonStringEnumMemberName("ai_extracted")]
    AiExtracted,

    /// <summary>Synced from another system. Machine-supplied: needs a person's confirmation.</summary>
    [JsonStringEnumMemberName("imported")]
    Imported,

    /// <summary>Copied from the firm's creditor library (issue 13.9); <see cref="ProvenanceEntry.LibraryCreditorId"/> names the row.</summary>
    [JsonStringEnumMemberName("library")]
    Library,

    /// <summary>Copied from the firm's client directory (ADR 0022); <see cref="ProvenanceEntry.ClientId"/> names the client.</summary>
    [JsonStringEnumMemberName("client")]
    Client,

    /// <summary>A debtor's own portal answer, accepted through the review queue (ADR 0023). Needs a person's confirmation.</summary>
    [JsonStringEnumMemberName("client_answered")]
    ClientAnswered,
}

/// <summary>
/// Where one field's value came from. <see cref="ConfirmedBy"/> and
/// <see cref="ConfirmedAt"/> are one act — a human said "yes, that is right"
/// at a moment — and exactly what accepting an extraction candidate writes.
/// </summary>
/// <remarks>
/// <see cref="ConfirmedAt"/> stays the RFC 3339 string the caller sent
/// (validated as a real UTC instant), not a <see cref="DateTimeOffset"/>: it
/// is the audit record of invariant 2, stored inside the JSONB provenance map
/// and echoed back byte for byte, and the "kept, never minted" check compares
/// entries for exact equality. <see cref="Locator"/> is kept as the JSON
/// object that was validated, unknown members included, as Python kept it.
/// </remarks>
public sealed record ProvenanceEntry(ProvenanceSource Source)
{
    /// <summary>The person who confirmed the value.</summary>
    public string? ConfirmedBy { get; init; }

    /// <summary>When they confirmed it — a UTC timestamp ending in <c>Z</c>.</summary>
    public string? ConfirmedAt { get; init; }

    /// <summary>The document the value was read from.</summary>
    public string? DocumentId { get; init; }

    /// <summary>Where on the page: <c>{document_id, page, region: {x, y, width, height}}</c>, region in page fractions.</summary>
    public JsonObject? Locator { get; init; }

    /// <summary>The extraction candidate the value was accepted from.</summary>
    public string? ExtractionId { get; init; }

    /// <summary>The extractor's confidence, 0 to 1.</summary>
    public double? Confidence { get; init; }

    /// <summary>The library creditor a <see cref="ProvenanceSource.Library"/> value was copied from. Shape and type only — never validated against the store.</summary>
    public string? LibraryCreditorId { get; init; }

    /// <summary>The firm client a <see cref="ProvenanceSource.Client"/> value was copied from. Shape and type only.</summary>
    public string? ClientId { get; init; }

    /// <summary>The case this value was COPIED FROM (issue 14.3), orthogonal to <see cref="Source"/>.</summary>
    public string? CopiedFromCaseId { get; init; }

    /// <inheritdoc />
    public bool Equals(ProvenanceEntry? other) =>
        other is not null
        && Source == other.Source
        && ConfirmedBy == other.ConfirmedBy
        && ConfirmedAt == other.ConfirmedAt
        && DocumentId == other.DocumentId
        && JsonNode.DeepEquals(Locator, other.Locator)
        && ExtractionId == other.ExtractionId
        && Nullable.Equals(Confidence, other.Confidence)
        && LibraryCreditorId == other.LibraryCreditorId
        && ClientId == other.ClientId
        && CopiedFromCaseId == other.CopiedFromCaseId;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Source, ConfirmedBy, ConfirmedAt, DocumentId, ExtractionId, LibraryCreditorId, ClientId, CopiedFromCaseId);
}
