using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.Debtors;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Cases;

/// <summary>
/// A case record (from <c>cases.py</c>). A CASE BELONGS TO A FIRM, not to
/// whoever opened it: <see cref="CreatedBy"/> is an audit fact that grants
/// nothing — the creator reaches the case through the assignment written with
/// it, exactly as any colleague does.
/// </summary>
/// <param name="Id">The server-minted uuid.</param>
/// <param name="FirmId">The owning firm.</param>
/// <param name="CreatedBy">Who opened it (an audit fact).</param>
/// <param name="Chapter">7, 11, 12 or 13 — the CURRENT chapter.</param>
/// <param name="District">The printed district name, derived from <see cref="Court"/> on every write.</param>
/// <param name="Status">Where the case is in its lifecycle.</param>
/// <param name="CreatedAt">When it was opened — the listing order.</param>
/// <param name="UpdatedAt">When it was last written.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "A case is the domain's name for the matter; no consumer is written in Visual Basic.")]
public sealed record Case(
    string Id,
    string FirmId,
    string CreatedBy,
    int Chapter,
    string District,
    CaseStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The court's CM/ECF code — null only on a row written before the registry existed.</summary>
    public string? Court { get; init; }

    /// <summary>The division's code, with <see cref="Court"/>.</summary>
    public string? Division { get; init; }

    /// <summary>
    /// THE PINS: form series id to <c>effective_date[+sequence]</c>, recorded
    /// by packet assembly — the ONE writer. Null while the case floats.
    /// </summary>
    public ValueDictionary<string, string>? FormRevisions { get; init; }

    /// <summary>The pinned <c>code/dollar-amounts</c> release, recorded with <see cref="FormRevisions"/>.</summary>
    public string? ConstantsSetId { get; init; }

    /// <summary>106C line 1's election; null until the preparer chooses.</summary>
    public ExemptionSet? ExemptionSet { get; init; }

    /// <summary>The petition date — a court date, never converted to UTC.</summary>
    public DateOnly? FiledAt { get; init; }

    /// <summary>The FIRST date set for the § 341(a) meeting — the deadline anchor.</summary>
    public DateOnly? Meeting341At { get; init; }

    /// <summary>The court's case number, in its own form.</summary>
    public string? CaseNumber { get; init; }

    /// <summary>The judge's name.</summary>
    public string? Judge { get; init; }

    /// <summary>The trustee's name.</summary>
    public string? Trustee { get; init; }

    /// <summary>The firm's own file number.</summary>
    public string? OfficeFileNumber { get; init; }

    /// <summary>When it left the working list — with <see cref="ArchivedBy"/>, or neither.</summary>
    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>Who archived it.</summary>
    public string? ArchivedBy { get; init; }

    /// <summary>When it was soft-deleted: unreachable through every read, nothing removed.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>Who deleted it.</summary>
    public string? DeletedBy { get; init; }

    /// <summary>Whether the case is archived.</summary>
    public bool Archived => ArchivedAt is not null;

    /// <summary>Whether the case is deleted.</summary>
    public bool Deleted => DeletedAt is not null;
}

/// <summary>
/// One person linked to one case. Its own row (not a list on the case), so
/// linking somebody and making the case appear in their listing are one
/// write. <see cref="CaseCreatedAt"/> is the CASE's creation, so every
/// listing orders the same way.
/// </summary>
/// <param name="CaseId">The case.</param>
/// <param name="Subject">The person linked.</param>
/// <param name="CaseCreatedAt">The case's creation, denormalised for the by-assignee order.</param>
/// <param name="AssignedAt">When they were linked.</param>
/// <param name="AssignedBy">Who linked them.</param>
public sealed record CaseAssignment(string CaseId, string Subject, DateTimeOffset CaseCreatedAt, DateTimeOffset AssignedAt, string AssignedBy);

/// <summary>A validated <c>{court, division}</c> pair — the registry knows it, and <see cref="District"/> is the name it prints.</summary>
public sealed record CourtReference(string Court, string Division, string District);

/// <summary>
/// Opening a case (<c>POST /v1/cases</c>). Every case starts at intake.
/// Whether each client is this firm's is the route's question.
/// </summary>
/// <param name="Chapter">7, 11, 12 or 13.</param>
/// <param name="Court">The court's CM/ECF code, from <c>GET /v1/courts</c>.</param>
/// <param name="Division">The court division's code.</param>
/// <param name="ClientIds">The firm clients it is opened for: Debtor 1, then Debtor 2 for a joint case.</param>
public sealed record NewCase(
    [property: AllowedValues(7, 11, 12, 13, ErrorMessage = CaseRules.ChapterMessage)] int Chapter,
    [property: Required(ErrorMessage = CaseRules.CourtRequired)] string? Court,
    [property: Required(ErrorMessage = CaseRules.DivisionRequired)] string? Division,
    [property: Required(ErrorMessage = NewCase.ClientRequired), MinLength(1, ErrorMessage = NewCase.ClientRequired), MaxLength(CaseRules.MaxCaseClients, ErrorMessage = "A case is opened for one client, or two filing jointly.")] IReadOnlyList<string>? ClientIds) : IValidatableObject
{
    private const string ClientRequired = "Choose the client this case is for.";

    /// <summary>Each client named once, by id; the court in the registry.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ClientIds!.Any(string.IsNullOrWhiteSpace))
        {
            yield return new ValidationResult("Each client must be a client id.", [nameof(ClientIds)]);
        }
        else if (ClientIds!.Distinct(StringComparer.Ordinal).Count() != ClientIds!.Count)
        {
            yield return new ValidationResult("The same client cannot be both debtors on one case.", [nameof(ClientIds)]);
        }

        if (CaseRules.CourtProblem(Court!, Division!) is { } problem)
        {
            yield return problem;
        }
    }
}

/// <summary>One entry of a client's case list: a case the caller may see, and which debtor of it the client is.</summary>
public sealed record ClientCase(Case Case, FilingRole FilingRole);

/// <summary>
/// Editing a case (<c>PATCH /v1/cases/{caseId}</c>): only the keys sent
/// change, and a body with none is a 400. The petition date, the § 341 date
/// and the four docket facts may be cleared — an explicit null, which
/// <see cref="Names"/> tells apart from a key not sent.
/// </summary>
public sealed record CaseChanges : IValidatableObject
{
    private readonly ImmutableHashSet<string> _named = [];

    /// <summary>A new chapter; a filed case's chapter changes only by a conversion (409).</summary>
    [AllowedValues(null, 7, 11, 12, 13, ErrorMessage = CaseRules.ChapterMessage)]
    public int? Chapter { get; init; }

    /// <summary>A new court's CM/ECF code, sent with <see cref="Division"/>.</summary>
    public string? Court { get; init; }

    /// <summary>A new division's code, sent with <see cref="Court"/>.</summary>
    public string? Division { get; init; }

    /// <summary>A status move — a lifecycle event; a move off the lifecycle is a 409.</summary>
    public CaseStatus? Status { get; init; }

    /// <summary>The court's date for a status move after filing (ADR 0027); today when absent. Only beside <see cref="Status"/>.</summary>
    public DateOnly? EffectiveOn { get; init; }

    /// <summary>Form 106C line 1's election.</summary>
    public ExemptionSet? ExemptionSet { get; init; }

    /// <summary>The petition date; null clears it.</summary>
    public DateOnly? FiledAt { get; init { field = value; _named = _named.Add(nameof(FiledAt)); } }

    /// <summary>The first § 341(a) meeting date; null clears it.</summary>
    public DateOnly? Meeting341At { get; init { field = value; _named = _named.Add(nameof(Meeting341At)); } }

    /// <summary>The court's case number; null clears it.</summary>
    [Text(40)]
    public string? CaseNumber { get; init { field = value; _named = _named.Add(nameof(CaseNumber)); } }

    /// <summary>The judge's name; null clears it.</summary>
    [Text(120)]
    public string? Judge { get; init { field = value; _named = _named.Add(nameof(Judge)); } }

    /// <summary>The trustee's name; null clears it.</summary>
    [Text(120)]
    public string? Trustee { get; init { field = value; _named = _named.Add(nameof(Trustee)); } }

    /// <summary>The firm's own file number; null clears it.</summary>
    [Text(40)]
    public string? OfficeFileNumber { get; init { field = value; _named = _named.Add(nameof(OfficeFileNumber)); } }

    /// <summary>Whether nothing would change.</summary>
    [JsonIgnore]
    public bool IsEmpty => _named.IsEmpty && Chapter is null && Court is null && Division is null && Status is null && EffectiveOn is null && ExemptionSet is null;

    /// <summary>
    /// Whether applying this changes anything the deadline engine reads — a
    /// date, a cleared date, or the chapter. The case route regenerates on exactly this.
    /// </summary>
    [JsonIgnore]
    public bool TouchesDeadlineAnchors => Names(nameof(FiledAt)) || Names(nameof(Meeting341At)) || Chapter is not null;

    /// <summary>Whether the request named <paramref name="member"/> — a clearable member set to null clears it.</summary>
    /// <param name="member">The C# member's name.</param>
    /// <returns>Whether it was sent.</returns>
    public bool Names(string member) => _named.Contains(member);

    /// <summary>The court pair travels together; a status move's date only beside a status; the § 341 meeting not before the petition.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Court is not null || Division is not null)
        {
            if (string.IsNullOrWhiteSpace(Court))
            {
                yield return new ValidationResult(CaseRules.CourtRequired, [nameof(Court)]);
            }
            else if (string.IsNullOrWhiteSpace(Division))
            {
                yield return new ValidationResult(CaseRules.DivisionRequired, [nameof(Division)]);
            }
            else if (CaseRules.CourtProblem(Court, Division) is { } problem)
            {
                yield return problem;
            }
        }

        if (EffectiveOn is not null && Status is null)
        {
            yield return new ValidationResult("Only a status move takes an effective date.", [nameof(EffectiveOn)]);
        }

        // Only checkable when both arrive together; against a stored date it
        // is a slip the deadline engine tolerates rather than a refused save.
        if (FiledAt is not null && Meeting341At is not null && Meeting341At < FiledAt)
        {
            yield return new ValidationResult("The § 341 meeting cannot precede the petition.", [nameof(Meeting341At)]);
        }
    }
}

/// <summary>One page of the cases a caller may see, newest first.</summary>
public sealed record CasePage(ValueCollection<Case> Cases, string? NextCursor);

/// <summary>
/// THE LIFECYCLE, AS DATA (issue 14.3 / #355; Python's <c>STATUSES</c>), in
/// the order a matter moves through it. A case STARTS at intake — there is no
/// prospect case.
/// </summary>
public enum CaseStatus
{
    /// <summary>Retained; the petition is being prepared.</summary>
    [JsonStringEnumMemberName("intake")]
    Intake,

    /// <summary>Retained; the preparer judges it ready.</summary>
    [JsonStringEnumMemberName("ready_to_file")]
    ReadyToFile,

    /// <summary>The petition is on the court's docket.</summary>
    [JsonStringEnumMemberName("filed")]
    Filed,

    /// <summary>An outcome: discharged.</summary>
    [JsonStringEnumMemberName("discharged")]
    Discharged,

    /// <summary>An outcome: dismissed.</summary>
    [JsonStringEnumMemberName("dismissed")]
    Dismissed,

    /// <summary>The court closed the case.</summary>
    [JsonStringEnumMemberName("closed")]
    Closed,
}

/// <summary>106C line 1: which § 522(b) set the debtor claims, named for its meaning (Python's <c>EXEMPTION_SETS</c>).</summary>
public enum ExemptionSet
{
    /// <summary>§ 522(b)(3): the domicile state's scheme plus the federal non-bankruptcy exemptions.</summary>
    [JsonStringEnumMemberName("state_and_federal_nonbankruptcy")]
    StateAndFederalNonbankruptcy,

    /// <summary>§ 522(b)(2): the § 522(d) list.</summary>
    [JsonStringEnumMemberName("federal")]
    Federal,
}
