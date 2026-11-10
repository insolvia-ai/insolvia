using System.Text.Json.Serialization;
using Insolvia.Api.Http;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Debtors;

namespace Insolvia.Api.Cases;

/// <summary>
/// A case as staff read it. No firm id (every reader is in that firm); the
/// court reference, the form pins, the exemption election, the deadline
/// anchors, the docket facts and the archive stamp are absent until set.
/// </summary>
/// <param name="Id">The case's id.</param>
/// <param name="CreatedBy">The subject who opened it — an audit fact that grants nothing.</param>
/// <param name="Chapter">The current chapter: 7, 11, 12 or 13.</param>
/// <param name="District">The printed district name, derived from the court.</param>
/// <param name="Status">Where the case is in its lifecycle.</param>
/// <param name="CreatedAt">When it was opened, ISO 8601 UTC with microseconds.</param>
/// <param name="UpdatedAt">When it was last written, ISO 8601 UTC with microseconds.</param>
/// <param name="Court">The court's CM/ECF code (from <c>GET /v1/courts</c>); absent only on a case opened before the registry.</param>
/// <param name="Division">The court division's code; absent with <paramref name="Court"/>.</param>
/// <param name="FormRevisions">The pinned revision of each official form series, by series id; absent while the case floats.</param>
/// <param name="ConstantsSetId">The pinned dollar-amounts release; absent with <paramref name="FormRevisions"/>.</param>
/// <param name="ExemptionSet">Form 106C line 1's election; absent until chosen.</param>
/// <param name="FiledAt">The petition date, YYYY-MM-DD; absent until filed.</param>
/// <param name="Meeting341At">The first date set for the § 341(a) meeting, YYYY-MM-DD; absent until set.</param>
/// <param name="CaseNumber">The court's case number; absent until set.</param>
/// <param name="Judge">The judge's name; absent until set.</param>
/// <param name="Trustee">The trustee's name; absent until set.</param>
/// <param name="OfficeFileNumber">The firm's own file number; absent until set.</param>
/// <param name="ArchivedAt">When the case left the working list, ISO 8601 UTC; absent unless archived.</param>
/// <param name="ArchivedBy">Who archived it; absent unless archived.</param>
public sealed record CaseView(
    string Id,
    string CreatedBy,
    int Chapter,
    string District,
    CaseStatus Status,
    string CreatedAt,
    string UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Court,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Division,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? FormRevisions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConstantsSetId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExemptionSet? ExemptionSet,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FiledAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Meeting341At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CaseNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Judge,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Trustee,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OfficeFileNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArchivedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArchivedBy)
{
    /// <summary>The view of <paramref name="stored"/>; the deletion stamp never reaches the wire.</summary>
    /// <param name="stored">The case.</param>
    /// <returns>The view.</returns>
    public static CaseView Of(Case stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return new(
            stored.Id,
            stored.CreatedBy,
            stored.Chapter,
            stored.District,
            stored.Status,
            CaseRules.Iso(stored.CreatedAt),
            CaseRules.Iso(stored.UpdatedAt),
            stored.Court,
            stored.Division,
            stored.FormRevisions,
            stored.ConstantsSetId,
            stored.ExemptionSet,
            stored.FiledAt is { } filed ? FlaskJson.Date(filed) : null,
            stored.Meeting341At is { } meeting ? FlaskJson.Date(meeting) : null,
            stored.CaseNumber,
            stored.Judge,
            stored.Trustee,
            stored.OfficeFileNumber,
            stored.ArchivedAt is { } archived ? CaseRules.Iso(archived) : null,
            stored.ArchivedBy);
    }
}

/// <summary>One page of the cases the caller may see, newest first.</summary>
/// <param name="Cases">The page.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; absent on the last page.</param>
public sealed record CaseList(
    IReadOnlyList<CaseView> Cases,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor);

/// <summary>One person linked to a case: a subject, never a name (the firm directory resolves it).</summary>
/// <param name="Subject">The linked person's subject.</param>
/// <param name="AssignedAt">When they were linked, ISO 8601 UTC with microseconds.</param>
/// <param name="AssignedBy">Who linked them.</param>
public sealed record AssigneeView(string Subject, string AssignedAt, string AssignedBy)
{
    /// <summary>The view of <paramref name="assignment"/>.</summary>
    /// <param name="assignment">The link.</param>
    /// <returns>The view.</returns>
    public static AssigneeView Of(CaseAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return new(assignment.Subject, CaseRules.Iso(assignment.AssignedAt), assignment.AssignedBy);
    }
}

/// <summary>The people linked to a case.</summary>
/// <param name="Assignees">The links.</param>
public sealed record AssigneeList(IReadOnlyList<AssigneeView> Assignees);

/// <summary>A case of a firm client, and which debtor on it the client is.</summary>
/// <param name="FilingRole">The client's role on the case.</param>
/// <param name="Case">The case.</param>
[SnakeCase]
public sealed record ClientCaseView(FilingRole FilingRole, CaseView Case);

/// <summary>A client's cases that the caller may see, newest first.</summary>
/// <param name="Cases">The cases.</param>
public sealed record ClientCaseList(IReadOnlyList<ClientCaseView> Cases);

/// <summary>
/// Opening a case (<c>POST /v1/cases</c>). Parsed by <c>CaseRules.ParseCreation</c>,
/// which owns validation; unknown keys are ignored, and a typed <c>district</c>
/// is refused. Every case starts at intake.
/// </summary>
/// <param name="Chapter">7, 11, 12 or 13.</param>
/// <param name="Court">The court's CM/ECF code, from <c>GET /v1/courts</c>.</param>
/// <param name="Division">The court division's code.</param>
/// <param name="ClientIds">The firm clients it is opened for: Debtor 1, then Debtor 2 for a joint case.</param>
[SnakeCase]
public sealed record CaseCreateRequest(int Chapter, string Court, string Division, IReadOnlyList<string> ClientIds);

/// <summary>
/// Editing a case (<c>PATCH /v1/cases/{caseId}</c>): only the keys sent are
/// changed, and a body with none is a 400. Parsed by <c>CaseRules.ParseUpdate</c>,
/// which owns validation.
/// </summary>
/// <param name="Chapter">A new chapter; a filed case's chapter changes only by a conversion (409).</param>
/// <param name="Court">A new court's CM/ECF code, sent with <paramref name="Division"/>.</param>
/// <param name="Division">A new division's code, sent with <paramref name="Court"/>.</param>
/// <param name="Status">A status move — a lifecycle event; a move off the lifecycle is a 409.</param>
/// <param name="EffectiveOn">The court's date for a status move after filing, YYYY-MM-DD; today when absent. Only beside <paramref name="Status"/>.</param>
/// <param name="ExemptionSet">Form 106C line 1's election.</param>
/// <param name="FiledAt">The petition date, YYYY-MM-DD; null clears it.</param>
/// <param name="Meeting341At">The § 341(a) meeting date, YYYY-MM-DD; null clears it.</param>
/// <param name="CaseNumber">The court's case number; null clears it.</param>
/// <param name="Judge">The judge's name; null clears it.</param>
/// <param name="Trustee">The trustee's name; null clears it.</param>
/// <param name="OfficeFileNumber">The firm's own file number; null clears it.</param>
[SnakeCase]
public sealed record CaseUpdateRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Chapter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Court,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Division,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CaseStatus? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveOn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExemptionSet? ExemptionSet,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FiledAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("meeting_341_at")] string? Meeting341At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CaseNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Judge,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Trustee,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OfficeFileNumber);

/// <summary>Archiving or restoring a case (<c>PUT /v1/cases/{caseId}/archived</c>).</summary>
/// <param name="Archived">True to archive, false to restore.</param>
public sealed record CaseArchiveRequest(bool Archived);
