using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.Cases;

namespace Insolvia.Api.Cases;

/// <summary>
/// One event of a case's lifecycle history (ADR 0027). The common members are
/// always present — <c>effectiveOn</c> null on a move between the pre-filing
/// statuses, which has no court date. The per-kind members are absent unless
/// the kind carries them.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="EffectiveOn">The court's date, YYYY-MM-DD; null for a preparation move with no court date.</param>
/// <param name="RecordedAt">When Insolvia recorded it, ISO 8601 UTC with microseconds.</param>
/// <param name="RecordedBy">The subject who recorded it, or the filing worker.</param>
/// <param name="Provenance">Who stands behind it.</param>
/// <param name="CandidateId">The court-notice candidate it was accepted from; absent otherwise.</param>
/// <param name="FromStatus">A status event's status before.</param>
/// <param name="ToStatus">A status event's status after.</param>
/// <param name="Chapter">A move into filed: the chapter it was filed (or reopened) under.</param>
/// <param name="FilingId">The electronic filing that made a status move.</param>
/// <param name="FromChapter">A conversion's chapter before.</param>
/// <param name="ToChapter">A conversion's chapter after.</param>
/// <param name="PlanVersionId">The plan version a confirmation ruling is about.</param>
public sealed record LifecycleEventView(
    LifecycleEventKind Kind,
    string? EffectiveOn,
    string RecordedAt,
    string RecordedBy,
    LifecycleProvenance Provenance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CandidateId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CaseStatus? FromStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CaseStatus? ToStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Chapter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FilingId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FromChapter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ToChapter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PlanVersionId)
{
    /// <summary>The view of <paramref name="recorded"/>.</summary>
    /// <param name="recorded">The event.</param>
    /// <returns>The view.</returns>
    public static LifecycleEventView Of(LifecycleEvent recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        return new(
            recorded.Kind,
            recorded.EffectiveOn is { } on ? WireFormat.Date(on) : null,
            CaseRules.Iso(recorded.RecordedAt),
            recorded.RecordedBy,
            recorded.Provenance,
            recorded.CandidateId,
            recorded.FromStatus,
            recorded.ToStatus,
            recorded.Chapter,
            recorded.FilingId,
            recorded.FromChapter,
            recorded.ToChapter,
            recorded.PlanVersionId);
    }
}

/// <summary>A case's lifecycle history.</summary>
/// <param name="Events">Every event, oldest first.</param>
public sealed record LifecycleList(IReadOnlyList<LifecycleEventView> Events);

/// <summary>
/// One status move, in the shape the app's <c>getCaseStatusHistory</c> reads:
/// the lifecycle history's status events only — a conversion or a ruling is
/// not a status move.
/// </summary>
/// <param name="ChangedAt">When it was recorded, ISO 8601 UTC with microseconds.</param>
/// <param name="ChangedBy">The subject who made it, or the filing worker.</param>
/// <param name="FromStatus">The status before.</param>
/// <param name="ToStatus">The status after.</param>
/// <param name="FilingId">The electronic filing that made the move; absent otherwise.</param>
public sealed record StatusChangeView(
    string ChangedAt,
    string ChangedBy,
    CaseStatus? FromStatus,
    CaseStatus? ToStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FilingId)
{
    /// <summary>The view of a status event.</summary>
    /// <param name="recorded">A <see cref="LifecycleEventKind.Status"/> event.</param>
    /// <returns>The view.</returns>
    public static StatusChangeView Of(LifecycleEvent recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        return new(CaseRules.Iso(recorded.RecordedAt), recorded.RecordedBy, recorded.FromStatus, recorded.ToStatus, recorded.FilingId);
    }
}

/// <summary>A case's status moves.</summary>
/// <param name="History">The moves, in recorded order.</param>
public sealed record StatusHistory(IReadOnlyList<StatusChangeView> History);

/// <summary>A recorded conversion: the case as written, and the event.</summary>
/// <param name="Case">The case under its new chapter.</param>
/// <param name="Event">The conversion event.</param>
public sealed record ConversionView(CaseView Case, LifecycleEventView Event);

/// <summary>
/// Recording a conversion (<c>POST /v1/cases/{caseId}/conversions</c>).
/// </summary>
/// <param name="ToChapter">The chapter the court converted the case to: 7, 11, 12 or 13, and not the current one.</param>
/// <param name="EffectiveOn">The court's date, YYYY-MM-DD; no earlier than the petition or the previous court event.</param>
public sealed record ConversionRequest(
    [property: AllowedValues(7, 11, 12, 13, ErrorMessage = CaseRules.ChapterMessage)] int ToChapter,
    [property: Required] DateOnly? EffectiveOn);
