using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Filings;

namespace Insolvia.Api.Filing;

/// <summary>
/// A filing run as a reader sees it: its ids and state, what the court said,
/// and the attorney's resolution of a hand-back — never the page's storage key.
/// </summary>
/// <param name="FilingId">The filing's id.</param>
/// <param name="ApprovalId">The approval it consumed.</param>
/// <param name="AttorneyId">The attorney whose credential it used.</param>
/// <param name="State">Where the run stands.</param>
/// <param name="Court">The court's code.</param>
/// <param name="ClaimedAt">When a worker claimed it, ISO 8601 UTC with milliseconds.</param>
/// <param name="UpdatedAt">When it was last written, ISO 8601 UTC with milliseconds.</param>
/// <param name="History">Every state entered, in order.</param>
/// <param name="Resolvable">Whether the attorney may still resolve it (a hand-back or unknown outcome with no resolution yet).</param>
/// <param name="Driver">The court driver that ran it; absent before one did.</param>
/// <param name="HandBack">Why the run stopped and what to do now; absent unless handed back.</param>
/// <param name="UnknownReason">Why the outcome is unknown; absent otherwise.</param>
/// <param name="Confirmation">What the court's confirmation screen said; absent until read.</param>
/// <param name="ReceiptDocumentId">The stored receipt document; absent until stored.</param>
/// <param name="FollowUps">Follow-up work the run left; absent when none.</param>
/// <param name="Resolution">The attorney's resolution of a hand-back; absent until resolved.</param>
public sealed record FilingView(
    string FilingId,
    string ApprovalId,
    string AttorneyId,
    FilingState State,
    string Court,
    string ClaimedAt,
    string UpdatedAt,
    IReadOnlyList<FilingStepView> History,
    bool Resolvable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Driver,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HandBackView? HandBack,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UnknownReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConfirmationView? Confirmation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReceiptDocumentId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? FollowUps,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ResolutionView? Resolution)
{
    /// <summary>The view of <paramref name="filing"/>.</summary>
    /// <param name="filing">The stored filing.</param>
    /// <returns>The view.</returns>
    public static FilingView Of(Insolvia.Domain.Filings.Filing filing)
    {
        ArgumentNullException.ThrowIfNull(filing);
        return new(
            filing.FilingId,
            filing.ApprovalId,
            filing.AttorneyId,
            filing.State,
            filing.Court,
            FilingApprovalRecords.Iso(filing.ClaimedAt),
            FilingApprovalRecords.Iso(filing.UpdatedAt),
            [.. filing.History.Select(step => new FilingStepView(step.State, FilingApprovalRecords.Iso(step.At)))],
            FilingRules.IsResolvable(filing),
            filing.Driver,
            filing.HandBack is { } note ? new(note.Reason, note.Stage, note.Title, note.Action, note.Link, note.CourtSaid) : null,
            filing.UnknownReason,
            filing.Confirmation is { } c ? new(c.CaseNumber, c.FiledAt, [.. c.DocketEntries], c.ReceiptNumber, c.FeeDue) : null,
            filing.ReceiptDocumentId,
            filing.FollowUps.Count > 0 ? [.. filing.FollowUps] : null,
            filing.Resolution is { } r
                ? new(
                    r.Outcome,
                    r.ResolvedBy,
                    FilingApprovalRecords.Iso(r.ResolvedAt),
                    FilingApprovalRecords.Iso(r.DocketCheckedAt),
                    r.CaseNumber,
                    r.FiledAt is { } filedAt ? WireFormat.Date(filedAt) : null,
                    r.ConfirmationDocumentId)
                : null);
    }
}

/// <summary>One entry of a filing's history.</summary>
/// <param name="State">The state entered.</param>
/// <param name="At">When, ISO 8601 UTC with milliseconds.</param>
public sealed record FilingStepView(FilingState State, string At);

/// <summary>Why a filing run stopped, and what the attorney does now.</summary>
/// <param name="Reason">The reason's code.</param>
/// <param name="Stage">The state the run stopped in.</param>
/// <param name="Title">A one-line headline, for a person.</param>
/// <param name="Action">What the attorney does now, for a person.</param>
/// <param name="Link">The app segment under <c>/cases/{id}/</c> to send them to.</param>
/// <param name="CourtSaid">The court's own message; absent when it said nothing.</param>
public sealed record HandBackView(
    string Reason,
    string Stage,
    string Title,
    string Action,
    string Link,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CourtSaid);

/// <summary>What the court's confirmation screen said, captured the moment it was read.</summary>
/// <param name="CaseNumber">The case number the court assigned.</param>
/// <param name="FiledAt">When the court says it was filed, as the court wrote it.</param>
/// <param name="DocketEntries">The docket entries the court listed.</param>
/// <param name="ReceiptNumber">The fee receipt's number; absent when none was shown.</param>
/// <param name="FeeDue">The fee still due; absent when none was shown.</param>
public sealed record ConfirmationView(
    string CaseNumber,
    string FiledAt,
    IReadOnlyList<string> DocketEntries,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReceiptNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FeeDue);

/// <summary>The attorney's answer to a hand-back: what the court's docket shows.</summary>
/// <param name="Outcome">Filed or not filed.</param>
/// <param name="ResolvedBy">Who resolved it.</param>
/// <param name="ResolvedAt">When, ISO 8601 UTC with milliseconds.</param>
/// <param name="DocketCheckedAt">When the attorney checked the docket, ISO 8601 UTC with milliseconds.</param>
/// <param name="CaseNumber">The case number on the docket; absent unless filed.</param>
/// <param name="FiledAt">The petition date, <c>yyyy-MM-dd</c>; absent unless filed.</param>
/// <param name="ConfirmationDocumentId">The uploaded confirmation document; absent when none.</param>
public sealed record ResolutionView(
    ResolutionOutcome Outcome,
    string ResolvedBy,
    string ResolvedAt,
    string DocketCheckedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CaseNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FiledAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConfirmationDocumentId);

/// <summary>
/// The attorney's resolution of a hand-back
/// (<c>POST /v1/cases/{caseId}/filings/{filingId}/resolution</c>). An unknown member is refused.
/// </summary>
/// <param name="Outcome"><c>filed</c> or <c>not_filed</c>.</param>
/// <param name="DocketChecked">Must be true: the attorney checked the court's docket.</param>
/// <param name="CaseNumber">The case number on the docket; filed only.</param>
/// <param name="FiledAt">The petition date, <c>yyyy-MM-dd</c>; filed only.</param>
/// <param name="ConfirmationDocumentId">An uploaded confirmation document; filed only.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FilingResolutionRequest(
    [property: Required(ErrorMessage = "Outcome must be filed or not_filed.")] ResolutionOutcome? Outcome,
    bool DocketChecked = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CaseNumber = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? FiledAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), MinLength(1, ErrorMessage = "Must be a document id.")] string? ConfirmationDocumentId = null)
    : IValidatableObject
{
    /// <summary>The resolution as the filing rules take it.</summary>
    /// <returns>The resolution.</returns>
    public ResolutionRequest ToResolution() => new(Outcome!.Value, CaseNumber, FiledAt, ConfirmationDocumentId);

    /// <summary>The docket was checked; a filed outcome names its number and date, a not-filed one neither.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!DocketChecked)
        {
            yield return new ValidationResult("Confirm you checked the court's own docket for this debtor.", [nameof(DocketChecked)]);
        }

        if (Outcome == ResolutionOutcome.Filed)
        {
            if (string.IsNullOrWhiteSpace(CaseNumber))
            {
                yield return new ValidationResult("Enter the case number the court's docket shows.", [nameof(CaseNumber)]);
            }
            else if (TextRules.Length(CaseNumber) > 40 || CaseNumbers.Parse(CaseNumber, requireOffice: true) is null)
            {
                yield return new ValidationResult("Enter the case number as the docket prints it, e.g. 6:26-bk-10000.", [nameof(CaseNumber)]);
            }

            if (FiledAt is null)
            {
                yield return new ValidationResult("Enter the petition date in YYYY-MM-DD form.", [nameof(FiledAt)]);
            }

            yield break;
        }

        if (CaseNumber is not null)
        {
            yield return new ValidationResult("A filing that is not on the docket has no case number.", [nameof(CaseNumber)]);
        }

        if (FiledAt is not null)
        {
            yield return new ValidationResult("A filing that is not on the docket has no filing details.", [nameof(FiledAt)]);
        }

        if (ConfirmationDocumentId is not null)
        {
            yield return new ValidationResult("A filing that is not on the docket has no filing details.", [nameof(ConfirmationDocumentId)]);
        }
    }
}
