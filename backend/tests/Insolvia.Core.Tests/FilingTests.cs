using Insolvia.Core.Cases;
using Insolvia.Core.Filings;
using Insolvia.Core.Memory;

namespace Insolvia.Core.Tests;

/// <summary>The electronic filing record and its memory store, translated from test_filings.py.</summary>
public sealed class FilingTests
{
    private const string CaseId = "00000000-0000-4000-8000-0000000000c1";
    private const string FilingId = "00000000-0000-4000-8000-0000000000f1";
    private const string Firm = "proof-firm-00000000";
    private const string Attorney = "00000000-0000-4000-8000-00000000a771";

    private static readonly DateTimeOffset Claimed = DateTimeOffset.Parse("2099-01-15T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Resolution Resolved = new(
        ResolutionOutcome.Filed,
        Attorney,
        DateTimeOffset.Parse("2099-01-16T09:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2099-01-16T09:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
        "6:26-bk-10000",
        new DateOnly(2099, 1, 15),
        "00000000-0000-4000-8000-0000000000d2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<FilingState> States() => [.. Wire.Members<FilingState>()];

    private static Filing MakeFiling(FilingState state = FilingState.Claimed, string attemptId = "attempt-1", Resolution? resolution = null) =>
        new(FilingId, CaseId, "00000000-0000-4000-8000-0000000000a1", Firm, Attorney, "00000000-0000-4000-8000-0000000000e1", "flmb", "3", state, attemptId, Claimed, 4071989400, Claimed)
        {
            History = [new FilingStep(FilingState.Claimed, Claimed)],
            Resolution = resolution,
        };

    private static Case MakeCase() =>
        new(CaseId, Firm, Attorney, 7, "Middle District of Florida", CaseStatus.ReadyToFile, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        {
            Court = "flmb",
            Division = "3",
            Meeting341At = new DateOnly(2099, 2, 20),
        };

    private static LifecycleWrite Filed(Case? current = null) =>
        CaseLifecycle.FileCase(current ?? MakeCase(), [], "6:26-bk-10000", "2099-01-15", CaseRules.FilingWorkerActor, FilingId);

    private static (MemoryCaseStore Cases, MemoryFilingStore Store) Stores()
    {
        var cases = new MemoryCaseStore();
        cases.Put(MakeCase());
        return (cases, new MemoryFilingStore(cases));
    }

    // ── The state machine ───────────────────────────────────────────

    [Fact]
    public void Every_state_has_a_transition_row() =>
        Assert.Equal(Wire.Members<FilingState>().ToHashSet(), FilingRules.Transitions.Keys.ToHashSet());

    [Fact]
    public void Terminal_states_have_no_way_out() =>
        Assert.All(FilingRules.Terminal, state => Assert.Empty(FilingRules.Transitions[state]));

    [Fact]
    public void Before_the_mark_every_state_may_hand_back() =>
        Assert.All(FilingRules.BeforeSubmit, state =>
        {
            Assert.True(FilingRules.MayTransition(state, FilingState.HandedBack));
            Assert.False(FilingRules.MayTransition(state, FilingState.OutcomeUnknown));
        });

    [Theory]
    [InlineData(FilingState.AtFinalSubmit)]
    [InlineData(FilingState.Submitted)]
    public void After_the_mark_nothing_can_be_called_a_hand_back(FilingState state)
    {
        Assert.False(FilingRules.MayTransition(state, FilingState.HandedBack));
        Assert.True(FilingRules.MayTransition(state, FilingState.OutcomeUnknown));
    }

    [Fact]
    public void The_only_way_to_submitted_is_through_the_mark() =>
        Assert.Equal([FilingState.AtFinalSubmit], FilingRules.Transitions.Where(pair => pair.Value.Contains(FilingState.Submitted)).Select(pair => pair.Key));

    [Fact]
    public void Nothing_goes_backwards()
    {
        var order = Wire.Members<FilingState>().ToList();
        foreach (var (state, targets) in FilingRules.Transitions)
        {
            Assert.All(targets, target => Assert.True(order.IndexOf(target) > order.IndexOf(state)));
        }
    }

    // test_the_item_round_trips_with_every_member, test_the_item_lives_in_the_cases_partition
    // and test_the_wire_shape_never_carries_the_page_s_storage_key: the item
    // shape and the JSON view; dropped (18.10 / 18.15).

    [Theory]
    [MemberData(nameof(States))]
    public void Only_a_hand_back_or_an_unknown_outcome_is_resolvable(FilingState state) =>
        Assert.Equal(state is FilingState.HandedBack or FilingState.OutcomeUnknown, FilingRules.IsResolvable(MakeFiling(state)));

    [Fact]
    public void A_resolved_filing_is_not_resolvable_again() =>
        Assert.False(FilingRules.IsResolvable(MakeFiling(FilingState.HandedBack, resolution: Resolved)));

    [Fact]
    public void Only_a_not_filed_resolution_frees_the_case()
    {
        var notFiled = Resolved with { Outcome = ResolutionOutcome.NotFiled, CaseNumber = null, FiledAt = null };
        Assert.True(FilingRules.FreesCase(MakeFiling(FilingState.HandedBack, resolution: notFiled)));
        Assert.True(FilingRules.FreesCase(MakeFiling(FilingState.OutcomeUnknown, resolution: notFiled)));
        Assert.False(FilingRules.FreesCase(MakeFiling(FilingState.HandedBack, resolution: Resolved)));
        Assert.False(FilingRules.FreesCase(MakeFiling(FilingState.HandedBack)));
        Assert.False(FilingRules.FreesCase(MakeFiling(FilingState.Filed)));
    }

    // ── Filing the case ─────────────────────────────────────────────

    [Fact]
    public void A_filing_moves_the_case_through_the_lifecycle_with_its_history_row()
    {
        var write = Filed();
        Assert.Equal((CaseStatus.Filed, "6:26-bk-10000", new DateOnly(2099, 1, 15)), (write.Case.Status, write.Case.CaseNumber, write.Case.FiledAt!.Value));
        Assert.Equal(new LifecycleVersion(CaseStatus.ReadyToFile, 7, null), write.ReadAt);
        var change = write.Event;
        Assert.Equal((LifecycleEventKind.Status, CaseStatus.ReadyToFile, CaseStatus.Filed), (change.Kind, change.FromStatus!.Value, change.ToStatus!.Value));
        Assert.Equal((CaseRules.FilingWorkerActor, FilingId, LifecycleProvenance.FilingWorker), (change.RecordedBy, change.FilingId, change.Provenance));
        Assert.Equal(write.Case.UpdatedAt, change.RecordedAt);

        // ADR 0027: the filed event carries the petition chapter and the petition date.
        Assert.Equal((7, new DateOnly(2099, 1, 15)), (change.Chapter!.Value, change.EffectiveOn!.Value));
        Assert.True(write.FilesTheCase);
    }

    // test_the_history_row_says_which_filing_and_round_trips: item and JSON; dropped.

    [Fact]
    public void A_filed_case_is_not_filed_again() =>
        Assert.Throws<ConflictException>(() => CaseLifecycle.FileCase(
            MakeCase() with { Status = CaseStatus.Filed, FiledAt = new DateOnly(2099, 1, 1), CaseNumber = "x" },
            [],
            "6:26-bk-10000",
            "2099-01-15",
            Attorney,
            FilingId,
            LifecycleProvenance.Manual));

    [Theory]
    [InlineData("", "2099-01-15", "case_number")]
    [InlineData("6:26-bk-1", "15/01/2099", "filed_at")]
    public void A_filing_needs_its_number_and_a_petition_date(string number, string date, string field) =>
        Assert.Contains(field, Assert.Throws<FieldValidationException>(() => CaseLifecycle.FileCase(MakeCase(), [], number, date, Attorney, FilingId, LifecycleProvenance.Manual)).Fields.Keys);

    // ── The memory store ────────────────────────────────────────────

    [Fact]
    public async Task A_filing_is_claimed_once()
    {
        var store = new MemoryFilingStore();
        Assert.True(await store.ClaimAsync(MakeFiling(), Ct));
        Assert.False(await store.ClaimAsync(MakeFiling(attemptId: "attempt-2"), Ct));
    }

    [Fact]
    public async Task A_transition_needs_the_state_and_the_attempt_it_read()
    {
        var store = new MemoryFilingStore();
        await store.ClaimAsync(MakeFiling(), Ct);
        Assert.False(await store.TransitionAsync(MakeFiling(FilingState.SignedIn), FilingState.Uploading, cancellationToken: Ct));
        Assert.False(await store.TransitionAsync(MakeFiling(FilingState.SignedIn, "attempt-2"), FilingState.Claimed, cancellationToken: Ct));
        Assert.True(await store.TransitionAsync(MakeFiling(FilingState.SignedIn), FilingState.Claimed, cancellationToken: Ct));
        Assert.False(await store.TransitionAsync(MakeFiling(FilingState.SignedIn), FilingState.Claimed, cancellationToken: Ct));
    }

    [Fact]
    public async Task Filing_the_record_files_the_case_in_the_same_write()
    {
        var (cases, store) = Stores();
        await store.ClaimAsync(MakeFiling(FilingState.Submitted), Ct);
        Assert.True(await store.TransitionAsync(MakeFiling(FilingState.Filed), FilingState.Submitted, Filed(), Ct));
        var filed = cases.Cases[CaseId];
        Assert.Equal((CaseStatus.Filed, "6:26-bk-10000", new DateOnly(2099, 1, 15)), (filed.Status, filed.CaseNumber, filed.FiledAt!.Value));
        Assert.Equal([FilingId], (await cases.LifecycleAsync(CaseId, Ct)).Select(row => row.FilingId));
        Assert.Equal(FilingState.Filed, (await store.GetAsync(CaseId, FilingId, Ct))!.State);
    }

    [Fact]
    public async Task The_case_write_sets_the_lifecycle_and_keeps_every_other_edit()
    {
        var (cases, store) = Stores();
        await store.ClaimAsync(MakeFiling(FilingState.Submitted), Ct);
        var write = Filed();
        cases.Put(cases.Cases[CaseId] with { Meeting341At = new DateOnly(2099, 3, 1) });
        Assert.True(await store.TransitionAsync(MakeFiling(FilingState.Filed), FilingState.Submitted, write, Ct));
        Assert.Equal(new DateOnly(2099, 3, 1), cases.Cases[CaseId].Meeting341At);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("chapter")]
    [InlineData("firm")]
    [InlineData("deleted")]
    public async Task A_case_that_moved_underneath_writes_nothing_at_all(string moved)
    {
        var (cases, store) = Stores();
        await store.ClaimAsync(MakeFiling(FilingState.Submitted), Ct);
        var write = Filed();
        var current = cases.Cases[CaseId];
        cases.Put(moved switch
        {
            "status" => current with { Status = CaseStatus.Intake },
            "chapter" => current with { Chapter = 13 },
            "firm" => current with { FirmId = "another-firm" },
            _ => current with { DeletedAt = Claimed, DeletedBy = "x" },
        });
        Assert.False(await store.TransitionAsync(MakeFiling(FilingState.Filed), FilingState.Submitted, write, Ct));
        Assert.Equal(FilingState.Submitted, (await store.GetAsync(CaseId, FilingId, Ct))!.State);
        Assert.Empty(await cases.LifecycleAsync(CaseId, Ct));
    }

    [Fact]
    public async Task An_event_that_landed_since_the_read_refuses_the_filed_write()
    {
        var (cases, store) = Stores();
        await store.ClaimAsync(MakeFiling(FilingState.Submitted), Ct);
        var write = Filed();

        // Back to intake and forward again: the status is what the worker
        // read, but the history is not.
        var ready = cases.Cases[CaseId];
        var intake = await cases.RecordAsync(CaseLifecycle.StatusMove(ready, ready with { Status = CaseStatus.Intake }, [], Attorney)!, Ct);
        var history = await cases.LifecycleAsync(CaseId, Ct);
        await cases.RecordAsync(CaseLifecycle.StatusMove(intake!, intake! with { Status = CaseStatus.ReadyToFile }, history, Attorney)!, Ct);

        Assert.False(await store.TransitionAsync(MakeFiling(FilingState.Filed), FilingState.Submitted, write, Ct));
        Assert.Equal(CaseStatus.ReadyToFile, cases.Cases[CaseId].Status);
        Assert.Equal(2, (await cases.LifecycleAsync(CaseId, Ct)).Count);
    }

    [Fact]
    public async Task Only_a_move_into_filed_rides_on_a_filing_write()
    {
        var (cases, store) = Stores();
        await store.ClaimAsync(MakeFiling(FilingState.Submitted), Ct);
        var ready = cases.Cases[CaseId];
        var back = CaseLifecycle.StatusMove(ready, ready with { Status = CaseStatus.Intake }, [], Attorney)!;
        await Assert.ThrowsAsync<ArgumentException>(() => store.TransitionAsync(MakeFiling(FilingState.Filed), FilingState.Submitted, back, Ct));
    }

    [Fact]
    public async Task A_filing_record_that_moved_writes_nothing_to_the_case()
    {
        var (cases, store) = Stores();
        await store.ClaimAsync(MakeFiling(FilingState.OutcomeUnknown), Ct);
        Assert.False(await store.TransitionAsync(MakeFiling(FilingState.Filed), FilingState.Submitted, Filed(), Ct));
        Assert.Equal(CaseStatus.ReadyToFile, cases.Cases[CaseId].Status);
    }

    [Fact]
    public async Task A_hand_back_is_resolved_once()
    {
        var (cases, store) = Stores();
        await store.ClaimAsync(MakeFiling(FilingState.HandedBack), Ct);
        Assert.True(await store.ResolveAsync(MakeFiling(FilingState.HandedBack, resolution: Resolved), FilingState.HandedBack, Filed(), Ct));
        Assert.Equal(CaseStatus.Filed, cases.Cases[CaseId].Status);
        var again = MakeFiling(FilingState.HandedBack, resolution: Resolved with { Outcome = ResolutionOutcome.NotFiled });
        Assert.False(await store.ResolveAsync(again, FilingState.HandedBack, cancellationToken: Ct));
        Assert.Equal(Resolved, (await store.GetAsync(CaseId, FilingId, Ct))!.Resolution);
    }

    [Fact]
    public async Task A_resolution_needs_the_state_it_read()
    {
        var (_, store) = Stores();
        await store.ClaimAsync(MakeFiling(FilingState.OutcomeUnknown), Ct);
        Assert.False(await store.ResolveAsync(MakeFiling(FilingState.HandedBack, resolution: Resolved), FilingState.HandedBack, cancellationToken: Ct));
    }

    // The nine FakeDynamoDb tests (the claim's and transition's conditions,
    // the three-item transaction, lost conditions as false, consistent reads)
    // are the DynamoDB adapter's; dropped — the memory store above is 18.10's oracle.

    [Fact]
    public void The_filing_vocabularies_keep_their_wire_strings()
    {
        Assert.Equal(["claimed", "signed_in", "uploading", "at_final_submit", "submitted", "filed", "handed_back", "outcome_unknown"], Wire.Names<FilingState>());
        Assert.Equal(["filed", "not_filed"], Wire.Names<ResolutionOutcome>());
    }
}
