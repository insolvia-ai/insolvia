using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Insolvia.Core.Cases;

// THE LIFECYCLE HISTORY (ADR 0027; #506 row 2). A case's history is ONE
// ordered series of events, replacing the STATUS# rows: status moves, the
// filing worker's `filed`, conversions and confirmation rulings. Every event
// is written through ONE writer — CaseLifecycle below — which owns the
// ordering, the date checks and the same-transaction rule. The case keeps
// `status` and `chapter` as the current values, written in the same
// transaction as the event and conditioned on the values read (the
// LifecycleVersion). Periods ("Chapter 13 from X to Y") are DERIVED from the
// events, never stored (LifecyclePeriods).

/// <summary>What kind of court fact an event records (ADR 0027).</summary>
public enum LifecycleEventKind
{
    /// <summary>The case moved between statuses — the only kind before filing.</summary>
    [JsonStringEnumMemberName("status")]
    Status,

    /// <summary>The court converted the case to another chapter.</summary>
    [JsonStringEnumMemberName("conversion")]
    Conversion,

    /// <summary>The court confirmed the Chapter 13 plan.</summary>
    [JsonStringEnumMemberName("confirmation")]
    Confirmation,

    /// <summary>The court denied confirmation.</summary>
    [JsonStringEnumMemberName("confirmation_denied")]
    ConfirmationDenied,
}

/// <summary>Who stands behind an event (ADR 0027 § Who writes an event).</summary>
public enum LifecycleProvenance
{
    /// <summary>Staff recorded it directly (the web app or an MCP harness).</summary>
    [JsonStringEnumMemberName("manual")]
    Manual,

    /// <summary>A person accepted a parsed court notice; <see cref="LifecycleEvent.CandidateId"/> names the candidate.</summary>
    [JsonStringEnumMemberName("court_notice")]
    CourtNotice,

    /// <summary>The filing worker's own <c>filed</c> write, the court's receipt behind it (ADR 0024).</summary>
    [JsonStringEnumMemberName("filing-worker")]
    FilingWorker,
}

/// <summary>
/// One event of a case's lifecycle history. The common members are on every
/// event; the rest belong to one kind each and are null on the others:
/// <list type="bullet">
/// <item><see cref="LifecycleEventKind.Status"/>: <see cref="FromStatus"/>,
/// <see cref="ToStatus"/>; <see cref="Chapter"/> on a move INTO filed (the
/// first one carries the PETITION chapter); <see cref="FilingId"/> when an
/// electronic filing made the move.</item>
/// <item><see cref="LifecycleEventKind.Conversion"/>: <see cref="FromChapter"/>, <see cref="ToChapter"/>.</item>
/// <item>The two confirmation kinds: <see cref="PlanVersionId"/> (#490, may be null until plan versions exist).</item>
/// </list>
/// </summary>
/// <param name="CaseId">The case.</param>
/// <param name="Kind">What happened.</param>
/// <param name="EffectiveOn">THE COURT'S DATE — a plain date, never converted
/// to UTC. Null only on a move between the two pre-filing statuses, which is
/// a preparation fact with no court date.</param>
/// <param name="RecordedAt">When Insolvia recorded it — strictly increasing per
/// case, and the case's own <c>updated_at</c> for that write.</param>
/// <param name="RecordedBy">The subject who recorded it, or <see cref="CaseRules.FilingWorkerActor"/>.</param>
/// <param name="Provenance">Who stands behind it.</param>
public sealed record LifecycleEvent(
    string CaseId,
    LifecycleEventKind Kind,
    DateOnly? EffectiveOn,
    DateTimeOffset RecordedAt,
    string RecordedBy,
    LifecycleProvenance Provenance)
{
    /// <summary>The candidate a court notice was accepted from.</summary>
    public string? CandidateId { get; init; }

    /// <summary>A status event's status before.</summary>
    public CaseStatus? FromStatus { get; init; }

    /// <summary>A status event's status after.</summary>
    public CaseStatus? ToStatus { get; init; }

    /// <summary>A move into filed: the chapter it was filed (or reopened) under.</summary>
    public int? Chapter { get; init; }

    /// <summary>The electronic filing that made a status move.</summary>
    public string? FilingId { get; init; }

    /// <summary>A conversion's chapter before.</summary>
    public int? FromChapter { get; init; }

    /// <summary>A conversion's chapter after.</summary>
    public int? ToChapter { get; init; }

    /// <summary>The plan version a confirmation ruling is about.</summary>
    public string? PlanVersionId { get; init; }

    /// <summary>Whether this event is a court fact (it has a court date) rather than a preparation move.</summary>
    public bool IsCourtEvent => EffectiveOn is not null;
}

/// <summary>
/// The version of a case a lifecycle write was checked against: the status
/// and chapter read, and the <see cref="LifecycleEvent.RecordedAt"/> of its
/// latest event (null when it has none). A store writes the event only while
/// all three still hold — a write raced by another lands nowhere.
/// </summary>
public sealed record LifecycleVersion(CaseStatus Status, int Chapter, DateTimeOffset? LatestRecordedAt);

/// <summary>
/// An event the writer accepted, with the case as it will be stored and the
/// version it was checked against. ONLY <see cref="CaseLifecycle"/> makes one
/// (the constructor is internal), and the stores write one only together with
/// its case — <c>ICaseStore.RecordAsync</c>, or the filing store's filed-case
/// half — so an event cannot be written apart from the case it describes.
/// </summary>
public sealed class LifecycleWrite
{
    internal LifecycleWrite(Case after, LifecycleEvent recorded, LifecycleVersion readAt)
    {
        Case = after;
        Event = recorded;
        ReadAt = readAt;
    }

    /// <summary>The case as it will be stored — its <c>updated_at</c> is the event's <see cref="LifecycleEvent.RecordedAt"/>.</summary>
    public Case Case { get; }

    /// <summary>The event.</summary>
    public LifecycleEvent Event { get; }

    /// <summary>The version the write is conditioned on.</summary>
    public LifecycleVersion ReadAt { get; }

    /// <summary>Whether this write is a move INTO filed — the only write the filing store's case half accepts.</summary>
    public bool FilesTheCase =>
        Event is { Kind: LifecycleEventKind.Status, ToStatus: CaseStatus.Filed } && !CaseRules.IsFiled(ReadAt.Status);
}

/// <summary>
/// THE ONE WRITER of the lifecycle history (ADR 0027 § One writer module).
/// Every way an event is made — a status move on the case PATCH, the filing
/// worker's <c>filed</c>, a hand-back resolved as filed, a conversion, a
/// confirmation ruling — goes through <see cref="Record"/>, which owns:
/// <list type="bullet">
/// <item><b>ordering</b>: an event is recorded strictly after the latest one
/// (a clock that has not moved is stepped one microsecond on), and the write
/// is conditioned on that latest event still being the latest;</item>
/// <item><b>the date checks</b>: a court date is no earlier than the petition
/// date nor than the case's previous court event;</item>
/// <item><b>the same-transaction rule</b>: what it returns is a
/// <see cref="LifecycleWrite"/>, which a store writes only together with the
/// case, conditioned on the status and chapter read.</item>
/// </list>
/// It enforces NO eligibility rule: by the time Insolvia learns of a
/// conversion the court has decided it (ADR 0027).
/// </summary>
public static class CaseLifecycle
{
    /// <summary>A lifecycle write raced another event. 409, like <see cref="CaseRules.StatusMoved"/>.</summary>
    public const string HistoryMoved = "This case's history changed since it was read — reload it and try again.";

    private const string EffectiveOnField = "effective_on";

    /// <summary>
    /// The event for a write that moved <paramref name="before"/> to
    /// <paramref name="after"/>'s status, or null when the status did not move
    /// (an edit is not an event). <paramref name="effectiveOn"/> is the court's
    /// date for a move after filing — today (UTC) when not given; a move INTO
    /// filed takes the petition date, and a move between the pre-filing
    /// statuses has none.
    /// </summary>
    public static LifecycleWrite? StatusMove(
        Case before,
        Case after,
        IReadOnlyList<LifecycleEvent> history,
        string recordedBy,
        DateOnly? effectiveOn = null,
        LifecycleProvenance provenance = LifecycleProvenance.Manual,
        string? filingId = null,
        string? candidateId = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Status == after.Status)
        {
            return effectiveOn is null
                ? null
                : throw Invalid("Only a status move takes an effective date.");
        }

        if (!(CaseRules.Transitions.TryGetValue(before.Status, out var allowed) && allowed.Contains(after.Status)))
        {
            throw new ConflictException($"A case cannot move from '{before.Status.ToWire()}' to '{after.Status.ToWire()}'.");
        }

        // Before filing the chapter is a preparation choice; once filed it
        // changes only through a conversion, never alongside a status move.
        if (CaseRules.IsFiled(before.Status) && after.Chapter != before.Chapter)
        {
            throw new ConflictException("A filed case's chapter changes only by a conversion.");
        }

        DateOnly? courtDate;
        int? chapter = null;
        if (!CaseRules.IsFiled(before.Status) && !CaseRules.IsFiled(after.Status))
        {
            courtDate = effectiveOn is null ? null : throw Invalid("A move before filing has no court date.");
        }
        else if (!CaseRules.IsFiled(before.Status))
        {
            // Into filed: the petition date IS the event's date, and the
            // chapter it was filed under is the petition chapter.
            var petitionDate = after.FiledAt ?? throw Invalid("A filed case needs its filed date.", "filed_at");
            courtDate = effectiveOn is null || effectiveOn == petitionDate
                ? petitionDate
                : throw Invalid("A move into filed takes the petition date.");
            chapter = after.Chapter;
        }
        else
        {
            courtDate = effectiveOn ?? DateOnly.FromDateTime(after.UpdatedAt.UtcDateTime);
            chapter = after.Status == CaseStatus.Filed ? after.Chapter : null;
        }

        var draft = new LifecycleEvent(before.Id, LifecycleEventKind.Status, courtDate, after.UpdatedAt, recordedBy, provenance)
        {
            FromStatus = before.Status,
            ToStatus = after.Status,
            Chapter = chapter,
            FilingId = filingId,
            CandidateId = candidateId,
        };
        return Record(before, after, draft, history);
    }

    /// <summary>
    /// The case moved to filed by an electronic filing (ADR 0024 PR 8) — or by
    /// a person resolving a hand-back as filed — through
    /// <see cref="CaseRules.ApplyChanges"/>, the same move a person's PATCH
    /// makes, refused where theirs would be. The event carries the petition
    /// chapter and the filing. The CALLER checks <see cref="CaseRules.IsFiled"/> first.
    /// </summary>
    public static LifecycleWrite FileCase(
        Case current,
        IReadOnlyList<LifecycleEvent> history,
        string caseNumber,
        string filedAt,
        string recordedBy,
        string filingId,
        LifecycleProvenance provenance = LifecycleProvenance.FilingWorker,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(caseNumber);
        ArgumentNullException.ThrowIfNull(filedAt);
        if (CaseRules.IsFiled(current.Status))
        {
            throw new ConflictException("This case is already filed.");
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var date = CaseRules.ParseCourtDate(JsonValue.Create(filedAt), "filed_at", errors);
        if (TextRules.Strip(caseNumber).Length == 0)
        {
            errors["case_number"] = "A filed case needs its case number.";
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        var after = CaseRules.ApplyChanges(
            current,
            new CaseChanges
            {
                Status = CaseStatus.Filed,
                FiledAt = date,
                Texts = new Dictionary<string, string?> { ["case_number"] = TextRules.Strip(caseNumber) }.ToValueDictionary(),
            },
            clock);
        return StatusMove(current, after, history, recordedBy, provenance: provenance, filingId: filingId)
            ?? throw new InvalidOperationException("the status moved: not filed -> filed");
    }

    /// <summary>
    /// A conversion (ADR 0027): the court moved the case to
    /// <paramref name="toChapter"/> on <paramref name="effectiveOn"/>. Allowed
    /// only while the case is <c>filed</c> (a closed case is reopened first);
    /// a conversion into 11 or 12 is RECORDED, not refused — it is a court fact.
    /// The case number and the petition date stay.
    /// </summary>
    public static LifecycleWrite Convert(
        Case current,
        IReadOnlyList<LifecycleEvent> history,
        int toChapter,
        DateOnly effectiveOn,
        string recordedBy,
        LifecycleProvenance provenance = LifecycleProvenance.Manual,
        string? candidateId = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Status != CaseStatus.Filed)
        {
            throw new ConflictException($"Only a filed case can be converted; this one is '{current.Status.ToWire()}'.");
        }

        if (!CaseRules.Chapters.Contains(toChapter))
        {
            throw Invalid("Chapter must be one of " + string.Join(", ", CaseRules.Chapters) + ".", "to_chapter");
        }

        if (toChapter == current.Chapter)
        {
            throw Invalid($"The case is already in Chapter {current.Chapter}.", "to_chapter");
        }

        var after = current with { Chapter = toChapter, UpdatedAt = Clock.Now(clock) };
        var draft = new LifecycleEvent(current.Id, LifecycleEventKind.Conversion, effectiveOn, after.UpdatedAt, recordedBy, provenance)
        {
            FromChapter = current.Chapter,
            ToChapter = toChapter,
            CandidateId = candidateId,
        };
        return Record(current, after, draft, history);
    }

    /// <summary>
    /// A Chapter 13 confirmation ruling (ADR 0027): <paramref name="confirmed"/>
    /// or denied, on <paramref name="effectiveOn"/>. Not a status — the case
    /// stays <c>filed</c>. A 409 unless the case is filed and its CURRENT
    /// chapter is 13.
    /// </summary>
    public static LifecycleWrite Rule(
        Case current,
        IReadOnlyList<LifecycleEvent> history,
        bool confirmed,
        DateOnly effectiveOn,
        string recordedBy,
        string? planVersionId = null,
        LifecycleProvenance provenance = LifecycleProvenance.Manual,
        string? candidateId = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Status != CaseStatus.Filed || current.Chapter != 13)
        {
            throw new ConflictException("Only a filed Chapter 13 case has a plan to confirm.");
        }

        var after = current with { UpdatedAt = Clock.Now(clock) };
        var kind = confirmed ? LifecycleEventKind.Confirmation : LifecycleEventKind.ConfirmationDenied;
        var draft = new LifecycleEvent(current.Id, kind, effectiveOn, after.UpdatedAt, recordedBy, provenance)
        {
            PlanVersionId = planVersionId,
            CandidateId = candidateId,
        };
        return Record(current, after, draft, history);
    }

    /// <summary>The version a write against <paramref name="current"/> and <paramref name="history"/> is conditioned on.</summary>
    public static LifecycleVersion VersionOf(Case current, IReadOnlyList<LifecycleEvent> history)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(history);
        return new LifecycleVersion(current.Status, current.Chapter, Latest(history)?.RecordedAt);
    }

    /// <summary>
    /// THE WRITER. Every public method above builds its event and ends here.
    /// </summary>
    private static LifecycleWrite Record(Case before, Case after, LifecycleEvent draft, IReadOnlyList<LifecycleEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrEmpty(draft.RecordedBy);
        if (history.Any(recorded => recorded.CaseId != before.Id))
        {
            throw new ArgumentException("the history passed is another case's", nameof(history));
        }

        if ((draft.Provenance == LifecycleProvenance.CourtNotice) != (draft.CandidateId is not null))
        {
            throw new ArgumentException("an event accepted from a court notice names its candidate, and only such an event does", nameof(draft));
        }

        if ((draft.Provenance == LifecycleProvenance.FilingWorker) != (draft.RecordedBy == CaseRules.FilingWorkerActor))
        {
            throw new ArgumentException("the filing worker records as itself, and nobody else does", nameof(draft));
        }

        // ORDERING: strictly after the latest event, so (case, recorded_at)
        // is unique and "oldest first" has one answer.
        var latest = Latest(history);
        var recordedAt = draft.RecordedAt;
        if (latest is not null && recordedAt <= latest.RecordedAt)
        {
            recordedAt = latest.RecordedAt.AddTicks(10);
        }

        // THE DATE CHECKS: a court date is no earlier than the petition date
        // or the case's previous court event.
        if (draft.EffectiveOn is { } on)
        {
            if (after.FiledAt is { } petition && on < petition)
            {
                throw Invalid($"The date cannot be earlier than the petition date ({Iso(petition)}).");
            }

            var previous = history.Where(recorded => recorded.IsCourtEvent).OrderBy(recorded => recorded.RecordedAt).LastOrDefault();
            if (previous?.EffectiveOn is { } last && on < last)
            {
                throw Invalid($"The date cannot be earlier than the case's previous event ({Iso(last)}).");
            }
        }

        var recorded = draft with { RecordedAt = recordedAt };
        return new LifecycleWrite(after with { UpdatedAt = recordedAt }, recorded, new LifecycleVersion(before.Status, before.Chapter, latest?.RecordedAt));
    }

    private static LifecycleEvent? Latest(IReadOnlyList<LifecycleEvent> history) =>
        history.Count == 0 ? null : history.MaxBy(recorded => recorded.RecordedAt);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static FieldValidationException Invalid(string message, string field = EffectiveOnField) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>One chapter the case was in, from a court date until the conversion that ended it (null: still).</summary>
public sealed record ChapterPeriod(int Chapter, DateOnly From, DateOnly? Until);

/// <summary>
/// THE PERIOD READER: "which chapter, from when to when" — derived from the
/// events, never stored (ADR 0027). Every method takes the history as the
/// store returns it (oldest first) and orders it again by
/// <see cref="LifecycleEvent.RecordedAt"/>, so the answer cannot depend on
/// the caller's order.
/// </summary>
public static class LifecyclePeriods
{
    /// <summary>The chapter the case was FILED under — on its first move into filed — or null before filing.</summary>
    public static int? PetitionChapter(IReadOnlyList<LifecycleEvent> history) =>
        Ordered(history).FirstOrDefault(IsFiling)?.Chapter;

    /// <summary>
    /// Every chapter period, oldest first: the petition chapter from the
    /// petition date, and one more per conversion, each ending on the date
    /// the next began. An outcome (discharge, dismissal, closing) ends no
    /// period — the case is still a case of that chapter. Empty before filing.
    /// </summary>
    public static IReadOnlyList<ChapterPeriod> ChapterPeriods(IReadOnlyList<LifecycleEvent> history)
    {
        var periods = new List<ChapterPeriod>();
        foreach (var recorded in Ordered(history))
        {
            if (periods.Count == 0 && IsFiling(recorded) && recorded is { Chapter: { } petition, EffectiveOn: { } filedOn })
            {
                periods.Add(new ChapterPeriod(petition, filedOn, null));
            }
            else if (periods.Count > 0 && recorded is { Kind: LifecycleEventKind.Conversion, ToChapter: { } to, EffectiveOn: { } on })
            {
                periods[^1] = periods[^1] with { Until = on };
                periods.Add(new ChapterPeriod(to, on, null));
            }
        }

        return periods;
    }

    /// <summary>The chapter the case was in on <paramref name="day"/> — a conversion's day belongs to the new chapter — or null outside every period.</summary>
    public static int? ChapterOn(IReadOnlyList<LifecycleEvent> history, DateOnly day) =>
        ChapterPeriods(history).LastOrDefault(period => period.From <= day && (period.Until is null || day < period.Until))?.Chapter;

    /// <summary>
    /// The latest confirmation ruling WITHIN THE CURRENT CHAPTER (recorded
    /// after the last conversion), or null — "is this case confirmed" is
    /// whether it is a <see cref="LifecycleEventKind.Confirmation"/>. A
    /// conversion leaves the old chapter's ruling behind.
    /// </summary>
    public static LifecycleEvent? CurrentRuling(IReadOnlyList<LifecycleEvent> history)
    {
        LifecycleEvent? ruling = null;
        foreach (var recorded in Ordered(history))
        {
            ruling = recorded.Kind switch
            {
                LifecycleEventKind.Conversion => null,
                LifecycleEventKind.Confirmation or LifecycleEventKind.ConfirmationDenied => recorded,
                _ => ruling,
            };
        }

        return ruling;
    }

    /// <summary>Whether the current chapter's plan is confirmed.</summary>
    public static bool IsConfirmed(IReadOnlyList<LifecycleEvent> history) =>
        CurrentRuling(history)?.Kind == LifecycleEventKind.Confirmation;

    private static bool IsFiling(LifecycleEvent recorded) =>
        recorded is { Kind: LifecycleEventKind.Status, ToStatus: CaseStatus.Filed } && recorded.FromStatus is { } from && !CaseRules.IsFiled(from);

    private static IEnumerable<LifecycleEvent> Ordered(IReadOnlyList<LifecycleEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return history.OrderBy(recorded => recorded.RecordedAt);
    }
}
