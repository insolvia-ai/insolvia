using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Insolvia.Core.Filings;

// The electronic filing record (ADR 0024 PR 7/8; from filings.py): one run
// of the filing worker against one approval, its state machine and history,
// what the court said, and — for a hand-back — the attorney's resolution.

/// <summary>One entry of the record's history: the state entered and when.</summary>
public sealed record FilingStep(FilingState State, DateTimeOffset At);

/// <summary>
/// Why the run stopped and what the attorney does now. <see cref="Stage"/> is
/// the state it stopped in; <see cref="CourtSaid"/> the court's own message;
/// <see cref="Link"/> the app segment under <c>/cases/&lt;id&gt;/</c>.
/// </summary>
public sealed record HandBackNote(string Reason, string Stage, string Title, string Action, string? CourtSaid = null, string Link = "packet");

/// <summary>What the court's confirmation screen said, captured the moment it was read.</summary>
public sealed record Confirmation(string CaseNumber, string FiledAt, ValueCollection<string> DocketEntries, string? ReceiptNumber = null, string? FeeDue = null, string? PageRef = null);

/// <summary>
/// The attorney's answer to a hand-back: what the court's docket shows.
/// <see cref="DocketCheckedAt"/> is required for either outcome; the case
/// number, petition date and confirmation document are the filed outcome's.
/// </summary>
public sealed record Resolution(
    ResolutionOutcome Outcome,
    string ResolvedBy,
    DateTimeOffset ResolvedAt,
    DateTimeOffset DocketCheckedAt,
    string? CaseNumber = null,
    DateOnly? FiledAt = null,
    string? ConfirmationDocumentId = null);

/// <summary>One filing run.</summary>
/// <param name="FilingId">The filing's id.</param>
/// <param name="CaseId">The case.</param>
/// <param name="ApprovalId">The approval it consumes.</param>
/// <param name="FirmId">The firm.</param>
/// <param name="AttorneyId">The attorney whose credential it uses.</param>
/// <param name="CredentialId">That credential.</param>
/// <param name="Court">The court's code.</param>
/// <param name="Division">The division's code.</param>
/// <param name="State">Where the run stands.</param>
/// <param name="AttemptId">This attempt — every conditional write checks it.</param>
/// <param name="ClaimedAt">When a worker claimed it.</param>
/// <param name="LeaseExpiresAt">When the claim's lease lapses, epoch seconds.</param>
/// <param name="UpdatedAt">When it was last written.</param>
public sealed record Filing(
    string FilingId,
    string CaseId,
    string ApprovalId,
    string FirmId,
    string AttorneyId,
    string CredentialId,
    string Court,
    string Division,
    FilingState State,
    string AttemptId,
    DateTimeOffset ClaimedAt,
    long LeaseExpiresAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Every state entered, in order.</summary>
    public ValueCollection<FilingStep> History { get; init; } = [];

    /// <summary>The court driver that ran it.</summary>
    public string? Driver { get; init; }

    /// <summary>Why it stopped, for a hand-back.</summary>
    public HandBackNote? HandBack { get; init; }

    /// <summary>Why the outcome is unknown.</summary>
    public string? UnknownReason { get; init; }

    /// <summary>What the court's confirmation said.</summary>
    public Confirmation? Confirmation { get; init; }

    /// <summary>The stored receipt document.</summary>
    public string? ReceiptDocumentId { get; init; }

    /// <summary>Follow-up work the run left.</summary>
    public ValueCollection<string> FollowUps { get; init; } = [];

    /// <summary>The attorney's resolution of a hand-back.</summary>
    public Resolution? Resolution { get; init; }
}

/// <summary>The filing state machine.</summary>
public static class FilingRules
{
    /// <summary>The states a run ends in.</summary>
    public static readonly IReadOnlySet<FilingState> Terminal =
        new HashSet<FilingState> { FilingState.Filed, FilingState.HandedBack, FilingState.OutcomeUnknown };

    /// <summary>The states before the final submit — a stop there never reached the court.</summary>
    public static readonly IReadOnlySet<FilingState> BeforeSubmit =
        new HashSet<FilingState> { FilingState.Claimed, FilingState.SignedIn, FilingState.Uploading };

    /// <summary>The states a person may resolve.</summary>
    public static readonly IReadOnlySet<FilingState> Resolvable =
        new HashSet<FilingState> { FilingState.HandedBack, FilingState.OutcomeUnknown };

    /// <summary>The allowed moves.</summary>
    public static readonly FrozenDictionary<FilingState, IReadOnlySet<FilingState>> Transitions =
        new Dictionary<FilingState, IReadOnlySet<FilingState>>
        {
            [FilingState.Claimed] = new HashSet<FilingState> { FilingState.SignedIn, FilingState.HandedBack },
            [FilingState.SignedIn] = new HashSet<FilingState> { FilingState.Uploading, FilingState.HandedBack },
            [FilingState.Uploading] = new HashSet<FilingState> { FilingState.AtFinalSubmit, FilingState.HandedBack },
            [FilingState.AtFinalSubmit] = new HashSet<FilingState> { FilingState.Submitted, FilingState.OutcomeUnknown },
            [FilingState.Submitted] = new HashSet<FilingState> { FilingState.Filed, FilingState.OutcomeUnknown },
            [FilingState.Filed] = new HashSet<FilingState>(),
            [FilingState.HandedBack] = new HashSet<FilingState>(),
            [FilingState.OutcomeUnknown] = new HashSet<FilingState>(),
        }.ToFrozenDictionary();

    /// <summary>Whether <paramref name="current"/> may move to <paramref name="target"/>.</summary>
    public static bool MayTransition(FilingState current, FilingState target) =>
        Transitions.TryGetValue(current, out var allowed) && allowed.Contains(target);

    /// <summary>Whether a person may still resolve this filing.</summary>
    public static bool IsResolvable(Filing filing)
    {
        ArgumentNullException.ThrowIfNull(filing);
        return Resolvable.Contains(filing.State) && filing.Resolution is null;
    }

    /// <summary>
    /// Whether this filing no longer stands in the way of a new approval: the
    /// attorney resolved it as never having reached the court.
    /// </summary>
    public static bool FreesCase(Filing filing)
    {
        ArgumentNullException.ThrowIfNull(filing);
        return Resolvable.Contains(filing.State) && filing.Resolution is { Outcome: ResolutionOutcome.NotFiled };
    }
}

/// <summary>Where an electronic filing run stands (Python's <c>FilingState</c>).</summary>
public enum FilingState
{
    /// <summary>A worker claimed the approval.</summary>
    [JsonStringEnumMemberName("claimed")]
    Claimed,

    /// <summary>Signed in to CM/ECF.</summary>
    [JsonStringEnumMemberName("signed_in")]
    SignedIn,

    /// <summary>Uploading the documents.</summary>
    [JsonStringEnumMemberName("uploading")]
    Uploading,

    /// <summary>At the final submit — the point past which the court may have the case.</summary>
    [JsonStringEnumMemberName("at_final_submit")]
    AtFinalSubmit,

    /// <summary>Submitted; awaiting the confirmation.</summary>
    [JsonStringEnumMemberName("submitted")]
    Submitted,

    /// <summary>The court confirmed the filing.</summary>
    [JsonStringEnumMemberName("filed")]
    Filed,

    /// <summary>Stopped before the court had it; handed to the attorney.</summary>
    [JsonStringEnumMemberName("handed_back")]
    HandedBack,

    /// <summary>Stopped after the submit with no confirmation read.</summary>
    [JsonStringEnumMemberName("outcome_unknown")]
    OutcomeUnknown,
}

/// <summary>What the court's docket shows, as the attorney resolved it (Python's <c>ResolutionOutcome</c>).</summary>
public enum ResolutionOutcome
{
    /// <summary>The case is on the docket.</summary>
    [JsonStringEnumMemberName("filed")]
    Filed,

    /// <summary>The case never reached the court.</summary>
    [JsonStringEnumMemberName("not_filed")]
    NotFiled,
}
