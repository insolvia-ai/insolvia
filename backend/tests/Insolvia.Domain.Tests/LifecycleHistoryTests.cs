using Insolvia.Core.Cases;
using static Insolvia.Core.Tests.CaseFixtures;

namespace Insolvia.Core.Tests;

/// <summary>
/// ADR 0027's lifecycle history (#506 row 2), built in .NET first: the one
/// writer (<see cref="CaseLifecycle"/>) — its ordering, its date checks and
/// the version a write is conditioned on — and the period reader
/// (<see cref="LifecyclePeriods"/>). The stores' half (same transaction,
/// conditions) is the conformance suite's in Insolvia.Data.Tests.
/// </summary>
public sealed class LifecycleHistoryTests
{
    private static readonly DateOnly Petition = new(2026, 9, 1);

    private static Case Intake() => NewCase().Case;

    private static Case Filed(int chapter = 7) =>
        Intake() with { Status = CaseStatus.Filed, Chapter = chapter, FiledAt = Petition, CaseNumber = "8:26-bk-01234" };

    private static Case Moved(Case current, string payload) =>
        CaseRules.ApplyChanges(current, CaseRules.ParseUpdate(Body.Of(payload)));

    // ── The PATCH's effective date ──────────────────────────────────

    [Fact]
    public void A_status_move_may_carry_its_court_date_and_nothing_else_may()
    {
        Assert.Equal(new DateOnly(2026, 12, 1), CaseRules.ParseUpdate(Body.Of("""{"status": "dismissed", "effective_on": "2026-12-01"}""")).EffectiveOn);
        Assert.Equal(["effective_on"], Assert.Throws<FieldValidationException>(() => CaseRules.ParseUpdate(Body.Of("""{"judge": "J", "effective_on": "2026-12-01"}"""))).Fields.Keys);
        Assert.Equal(["effective_on"], Assert.Throws<FieldValidationException>(() => CaseRules.ParseUpdate(Body.Of("""{"status": "dismissed", "effective_on": "December"}"""))).Fields.Keys);
    }

    // ── Status events ───────────────────────────────────────────────

    [Fact]
    public void A_move_before_filing_is_an_event_without_a_court_date()
    {
        var before = Intake();
        var write = CaseLifecycle.StatusMove(before, Moved(before, """{"status": "ready_to_file"}"""), [], Ids.Alice)!;
        Assert.Equal((LifecycleEventKind.Status, LifecycleProvenance.Manual), (write.Event.Kind, write.Event.Provenance));
        Assert.Null(write.Event.EffectiveOn);
        Assert.Null(write.Event.Chapter);
        Assert.False(write.Event.IsCourtEvent);
        Assert.Throws<FieldValidationException>(() => CaseLifecycle.StatusMove(before, Moved(before, """{"status": "ready_to_file"}"""), [], Ids.Alice, Petition));
    }

    [Fact]
    public void The_filed_event_carries_the_petition_chapter_and_the_petition_date()
    {
        var before = Intake() with { Chapter = 13 };
        var after = Moved(before, """{"status": "filed", "filed_at": "2026-09-01", "case_number": "8:26-bk-01234"}""");
        var write = CaseLifecycle.StatusMove(before, after, [], Ids.Alice)!;
        Assert.Equal((13, Petition, CaseStatus.Filed), (write.Event.Chapter!.Value, write.Event.EffectiveOn!.Value, write.Event.ToStatus!.Value));
        Assert.True(write.FilesTheCase);
        Assert.Equal(13, LifecyclePeriods.PetitionChapter([write.Event]));

        // A date that is not the petition's is refused on the way in.
        var error = Assert.Throws<FieldValidationException>(() => CaseLifecycle.StatusMove(before, after, [], Ids.Alice, Petition.AddDays(1)));
        Assert.Equal(["effective_on"], error.Fields.Keys);
    }

    [Fact]
    public void A_chapter_chosen_in_the_same_write_as_filing_is_the_petition_chapter()
    {
        var before = Intake();
        var after = Moved(before, """{"status": "filed", "chapter": 13, "filed_at": "2026-09-01", "case_number": "8:26-bk-01234"}""");
        Assert.Equal(13, CaseLifecycle.StatusMove(before, after, [], Ids.Alice)!.Event.Chapter);
    }

    [Fact]
    public void A_move_after_filing_defaults_to_the_day_it_is_recorded()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 3, 23, 59, 0, TimeSpan.Zero));
        var before = Filed();
        var after = CaseRules.ApplyChanges(before, CaseRules.ParseUpdate(Body.Of("""{"status": "dismissed"}""")), clock);
        var write = CaseLifecycle.StatusMove(before, after, [], Ids.Alice)!;
        Assert.Equal(new DateOnly(2026, 11, 3), write.Event.EffectiveOn);
        Assert.Null(write.Event.Chapter);
    }

    [Fact]
    public void A_court_date_before_the_petition_or_the_previous_event_is_refused()
    {
        var before = Filed();
        var dismissed = Moved(before, """{"status": "dismissed"}""");
        var early = Assert.Throws<FieldValidationException>(() => CaseLifecycle.StatusMove(before, dismissed, [], Ids.Alice, Petition.AddDays(-1)));
        Assert.Contains("petition date (2026-09-01)", early.Fields["effective_on"], StringComparison.Ordinal);

        var converted = CaseLifecycle.Convert(before, [], 13, new DateOnly(2026, 10, 15), Ids.Alice);
        var history = new[] { converted.Event };
        var stale = Assert.Throws<FieldValidationException>(() =>
            CaseLifecycle.StatusMove(converted.Case, Moved(converted.Case, """{"status": "dismissed"}"""), history, Ids.Alice, new DateOnly(2026, 10, 14)));
        Assert.Contains("previous event (2026-10-15)", stale.Fields["effective_on"], StringComparison.Ordinal);

        // The same day is not earlier.
        Assert.NotNull(CaseLifecycle.StatusMove(converted.Case, Moved(converted.Case, """{"status": "dismissed"}"""), history, Ids.Alice, new DateOnly(2026, 10, 15)));
    }

    [Fact]
    public void A_pre_filing_move_recorded_late_does_not_bind_the_court_dates()
    {
        // Data entry of a case filed before it reached Insolvia: the
        // preparation moves are recorded today, the petition is weeks old.
        var before = Intake();
        var ready = CaseLifecycle.StatusMove(before, Moved(before, """{"status": "ready_to_file"}"""), [], Ids.Alice)!;
        var filed = CaseLifecycle.StatusMove(ready.Case, Moved(ready.Case, """{"status": "filed", "filed_at": "2020-01-02", "case_number": "8:20-bk-00001"}"""), [ready.Event], Ids.Alice)!;
        Assert.Equal(new DateOnly(2020, 1, 2), filed.Event.EffectiveOn);
    }

    [Fact]
    public void A_filed_case_does_not_change_chapter_alongside_a_status_move()
    {
        var before = Filed();
        Assert.Throws<ConflictException>(() => CaseLifecycle.StatusMove(before, before with { Status = CaseStatus.Dismissed, Chapter = 13 }, [], Ids.Alice));
    }

    [Fact]
    public void The_writer_refuses_a_move_off_the_map_whoever_built_the_case() =>
        Assert.Throws<ConflictException>(() => CaseLifecycle.StatusMove(Intake(), Intake() with { Status = CaseStatus.Discharged }, [], Ids.Alice));

    // ── Ordering ────────────────────────────────────────────────────

    [Fact]
    public void An_event_is_recorded_strictly_after_the_latest_and_the_case_carries_its_instant()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var before = Intake();
        var first = CaseLifecycle.StatusMove(before, CaseRules.ApplyChanges(before, new CaseChanges { Status = CaseStatus.ReadyToFile }, clock), [], Ids.Alice)!;
        var back = CaseRules.ApplyChanges(first.Case, new CaseChanges { Status = CaseStatus.Intake }, clock);
        var second = CaseLifecycle.StatusMove(first.Case, back, [first.Event], Ids.Alice)!;

        Assert.Equal(first.Event.RecordedAt.AddTicks(10), second.Event.RecordedAt);
        Assert.Equal(second.Event.RecordedAt, second.Case.UpdatedAt);
        Assert.Equal(new LifecycleVersion(CaseStatus.ReadyToFile, 7, first.Event.RecordedAt), second.ReadAt);
        Assert.Equal(second.ReadAt, CaseLifecycle.VersionOf(first.Case, [first.Event]));
    }

    [Fact]
    public void The_writer_refuses_another_case_s_history()
    {
        var before = Intake();
        var stranger = CaseLifecycle.StatusMove(Intake(), Intake() with { Status = CaseStatus.ReadyToFile }, [], Ids.Alice)!.Event;
        Assert.Throws<ArgumentException>(() => CaseLifecycle.StatusMove(before, before with { Status = CaseStatus.ReadyToFile }, [stranger], Ids.Alice));
    }

    [Fact]
    public void Provenance_names_its_source_and_only_its_source()
    {
        var before = Filed();
        var after = Moved(before, """{"status": "discharged"}""");
        Assert.Throws<ArgumentException>(() => CaseLifecycle.StatusMove(before, after, [], Ids.Alice, provenance: LifecycleProvenance.CourtNotice));
        Assert.Throws<ArgumentException>(() => CaseLifecycle.StatusMove(before, after, [], Ids.Alice, candidateId: "candidate-1"));
        Assert.Throws<ArgumentException>(() => CaseLifecycle.StatusMove(before, after, [], Ids.Alice, provenance: LifecycleProvenance.FilingWorker));
        Assert.Throws<ArgumentException>(() => CaseLifecycle.StatusMove(before, after, [], CaseRules.FilingWorkerActor));
        var accepted = CaseLifecycle.StatusMove(before, after, [], Ids.Alice, provenance: LifecycleProvenance.CourtNotice, candidateId: "candidate-1")!;
        Assert.Equal("candidate-1", accepted.Event.CandidateId);
    }

    // ── Conversions and confirmations: the domain holds them ────────

    [Fact]
    public void A_conversion_moves_the_current_chapter_and_keeps_the_petition()
    {
        var before = Filed(13);
        var write = CaseLifecycle.Convert(before, [], 7, new DateOnly(2027, 2, 1), Ids.Alice);
        Assert.Equal((7, Petition, "8:26-bk-01234", CaseStatus.Filed), (write.Case.Chapter, write.Case.FiledAt!.Value, write.Case.CaseNumber, write.Case.Status));
        Assert.Equal((LifecycleEventKind.Conversion, 13, 7), (write.Event.Kind, write.Event.FromChapter!.Value, write.Event.ToChapter!.Value));
        Assert.Equal(new LifecycleVersion(CaseStatus.Filed, 13, null), write.ReadAt);
        Assert.False(write.FilesTheCase);
    }

    [Theory]
    [InlineData(CaseStatus.Intake)]
    [InlineData(CaseStatus.Discharged)]
    [InlineData(CaseStatus.Dismissed)]
    [InlineData(CaseStatus.Closed)]
    public void Only_a_filed_case_converts(CaseStatus status) =>
        Assert.Throws<ConflictException>(() => CaseLifecycle.Convert(Filed() with { Status = status }, [], 13, Petition, Ids.Alice));

    [Fact]
    public void A_conversion_must_change_the_chapter_and_may_go_anywhere_the_court_sent_it()
    {
        Assert.Throws<FieldValidationException>(() => CaseLifecycle.Convert(Filed(7), [], 7, Petition, Ids.Alice));
        Assert.Throws<FieldValidationException>(() => CaseLifecycle.Convert(Filed(7), [], 9, Petition, Ids.Alice));
        Assert.Equal(11, CaseLifecycle.Convert(Filed(7), [], 11, Petition, Ids.Alice).Case.Chapter);
    }

    [Fact]
    public void A_confirmation_ruling_is_for_a_filed_chapter_13_case_and_is_not_a_status()
    {
        Assert.Throws<ConflictException>(() => CaseLifecycle.Rule(Filed(7), [], true, Petition, Ids.Alice));
        Assert.Throws<ConflictException>(() => CaseLifecycle.Rule(Filed(13) with { Status = CaseStatus.Dismissed }, [], true, Petition, Ids.Alice));
        var write = CaseLifecycle.Rule(Filed(13), [], false, new DateOnly(2026, 12, 1), Ids.Alice, planVersionId: "plan-1");
        Assert.Equal((LifecycleEventKind.ConfirmationDenied, "plan-1", CaseStatus.Filed), (write.Event.Kind, write.Event.PlanVersionId, write.Case.Status));
    }

    // ── The period reader ───────────────────────────────────────────

    [Fact]
    public void Periods_are_derived_from_the_filing_and_every_conversion()
    {
        var history = Converted13To7To13();
        Assert.Equal(
            [
                new ChapterPeriod(13, Petition, new DateOnly(2027, 1, 10)),
                new ChapterPeriod(7, new DateOnly(2027, 1, 10), new DateOnly(2027, 3, 1)),
                new ChapterPeriod(13, new DateOnly(2027, 3, 1), null),
            ],
            LifecyclePeriods.ChapterPeriods(history));
        Assert.Equal(13, LifecyclePeriods.PetitionChapter(history));
        Assert.Null(LifecyclePeriods.ChapterOn(history, Petition.AddDays(-1)));
        Assert.Equal(13, LifecyclePeriods.ChapterOn(history, Petition));
        Assert.Equal(7, LifecyclePeriods.ChapterOn(history, new DateOnly(2027, 1, 10)));
        Assert.Equal(7, LifecyclePeriods.ChapterOn(history, new DateOnly(2027, 2, 28)));
        Assert.Equal(13, LifecyclePeriods.ChapterOn(history, new DateOnly(2030, 1, 1)));

        // The caller's order does not matter.
        Assert.Equal(LifecyclePeriods.ChapterPeriods(history), LifecyclePeriods.ChapterPeriods([.. history.AsEnumerable().Reverse()]));
    }

    [Fact]
    public void An_outcome_ends_no_period_and_a_reopening_is_not_a_second_petition()
    {
        var filed = FileFromIntake(7);
        var closed = CaseLifecycle.StatusMove(filed.Case, Moved(filed.Case, """{"status": "closed"}"""), [filed.Event], Ids.Alice, new DateOnly(2027, 1, 1))!;
        var reopened = CaseLifecycle.StatusMove(closed.Case, Moved(closed.Case, """{"status": "filed"}"""), [filed.Event, closed.Event], Ids.Alice, new DateOnly(2027, 6, 1))!;
        LifecycleEvent[] history = [filed.Event, closed.Event, reopened.Event];

        Assert.Equal(7, reopened.Event.Chapter);
        Assert.False(reopened.FilesTheCase);
        Assert.Equal([new ChapterPeriod(7, Petition, null)], LifecyclePeriods.ChapterPeriods(history));
        Assert.Equal(7, LifecyclePeriods.PetitionChapter(history));
    }

    [Fact]
    public void Nothing_has_a_period_before_filing() =>
        Assert.Empty(LifecyclePeriods.ChapterPeriods([CaseLifecycle.StatusMove(Intake(), Intake() with { Status = CaseStatus.ReadyToFile }, [], Ids.Alice)!.Event]));

    [Fact]
    public void Confirmation_is_read_within_the_current_chapter()
    {
        var filed = FileFromIntake(13);
        var confirmed = CaseLifecycle.Rule(filed.Case, [filed.Event], true, new DateOnly(2026, 12, 1), Ids.Alice);
        LifecycleEvent[] history = [filed.Event, confirmed.Event];
        Assert.True(LifecyclePeriods.IsConfirmed(history));

        var to7 = CaseLifecycle.Convert(confirmed.Case, history, 7, new DateOnly(2027, 1, 10), Ids.Alice);
        history = [.. history, to7.Event];
        Assert.False(LifecyclePeriods.IsConfirmed(history));
        Assert.Null(LifecyclePeriods.CurrentRuling(history));

        var back = CaseLifecycle.Convert(to7.Case, history, 13, new DateOnly(2027, 3, 1), Ids.Alice);
        var denied = CaseLifecycle.Rule(back.Case, [.. history, back.Event], false, new DateOnly(2027, 4, 1), Ids.Alice);
        history = [.. history, back.Event, denied.Event];
        Assert.Equal(LifecycleEventKind.ConfirmationDenied, LifecyclePeriods.CurrentRuling(history)!.Kind);
        Assert.False(LifecyclePeriods.IsConfirmed(history));
    }

    private static LifecycleWrite FileFromIntake(int chapter)
    {
        var before = Intake() with { Chapter = chapter };
        return CaseLifecycle.StatusMove(before, Moved(before, """{"status": "filed", "filed_at": "2026-09-01", "case_number": "8:26-bk-01234"}"""), [], Ids.Alice)!;
    }

    private static LifecycleEvent[] Converted13To7To13()
    {
        var filed = FileFromIntake(13);
        var to7 = CaseLifecycle.Convert(filed.Case, [filed.Event], 7, new DateOnly(2027, 1, 10), Ids.Alice);
        var to13 = CaseLifecycle.Convert(to7.Case, [filed.Event, to7.Event], 13, new DateOnly(2027, 3, 1), Ids.Alice);
        return [filed.Event, to7.Event, to13.Event];
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
