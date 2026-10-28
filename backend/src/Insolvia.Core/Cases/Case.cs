using System.Text.Json.Serialization;
using Insolvia.Core.Debtors;

namespace Insolvia.Core.Cases;

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
/// One move of a case through its lifecycle — who, when, from what to what
/// (#355). Written in the same transaction as the case record it describes,
/// so the history cannot disagree with the status.
/// </summary>
/// <param name="CaseId">The case.</param>
/// <param name="ChangedAt">When — the case's own <c>updated_at</c> for that write.</param>
/// <param name="ChangedBy">Who: a subject, or <see cref="CaseRules.FilingWorkerActor"/>.</param>
/// <param name="FromStatus">The status before.</param>
/// <param name="ToStatus">The status after.</param>
/// <param name="FilingId">The electronic filing that made the move, when one did.</param>
public sealed record StatusChange(string CaseId, DateTimeOffset ChangedAt, string ChangedBy, CaseStatus FromStatus, CaseStatus ToStatus, string? FilingId = null);

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
/// A validated creation request. <see cref="ClientIds"/> are the firm clients
/// it is opened for, Debtor 1 first (ADR 0022) — whether each is this firm's
/// is the route's question, not the parser's.
/// </summary>
public sealed record CaseDraft(int Chapter, CourtReference Court, ValueCollection<string> ClientIds);

/// <summary>One entry of a client's case list: a case the caller may see, and which debtor of it the client is.</summary>
public sealed record ClientCase(Case Case, FilingRole FilingRole);

/// <summary>
/// A validated PATCH body. Null means "leave unchanged". The two dates are the
/// one place that is not enough — an explicit JSON null clears one, recorded
/// as <see cref="ClearFiledAt"/>/<see cref="ClearMeeting341At"/>.
/// <see cref="Texts"/> holds the post-filing text fields the body NAMED: a
/// value sets, null clears, absent from the map is unchanged.
/// </summary>
public sealed record CaseChanges
{
    /// <summary>A new chapter.</summary>
    public int? Chapter { get; init; }

    /// <summary>A new court reference (court, division and district together).</summary>
    public CourtReference? Court { get; init; }

    /// <summary>A status move.</summary>
    public CaseStatus? Status { get; init; }

    /// <summary>A new 106C election.</summary>
    public ExemptionSet? ExemptionSet { get; init; }

    /// <summary>A new petition date.</summary>
    public DateOnly? FiledAt { get; init; }

    /// <summary>A new § 341 date.</summary>
    public DateOnly? Meeting341At { get; init; }

    /// <summary>Clear the petition date.</summary>
    public bool ClearFiledAt { get; init; }

    /// <summary>Clear the § 341 date.</summary>
    public bool ClearMeeting341At { get; init; }

    /// <summary>The post-filing text fields named, by their wire names.</summary>
    public ValueDictionary<string, string?> Texts { get; init; } = ValueCollections.EmptyDictionary<string, string?>();

    /// <summary>
    /// Whether applying this changes anything the deadline engine reads — a
    /// date, a cleared date, or the chapter. The case route regenerates on exactly this.
    /// </summary>
    public bool TouchesDeadlineAnchors =>
        FiledAt is not null || Meeting341At is not null || ClearFiledAt || ClearMeeting341At || Chapter is not null;
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
