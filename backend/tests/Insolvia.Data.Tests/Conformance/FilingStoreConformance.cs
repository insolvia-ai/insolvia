using Insolvia.Core.Cases;
using Insolvia.Core.Filings;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What every <see cref="Core.Ports.IFilingStore"/> must do (ADR 0024 PR 7/8):
/// a claim once, each transition from the state and attempt it read, a
/// resolution once — and, with a filed case, the case's move into filed and
/// its lifecycle event in the SAME write, refused as a whole when the case
/// moved underneath (status, chapter, history, deletion). Two racing writers
/// from one read: exactly one wins.
/// </summary>
public abstract class FilingStoreConformance : CaseConformance
{
    private const string AttemptId = "attempt-1";
    private static readonly DateTimeOffset Claimed = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_filing_is_claimed_once_and_reads_back_whole()
    {
        var owner = await CreateCaseAsync();
        var filing = MakeFiling(owner.Id) with
        {
            Driver = "cmecf-flmb",
            HandBack = new HandBackNote("court_rejected", "uploading", "The court refused the upload", "Upload again", CourtSaid: "File too large"),
            Confirmation = new Confirmation("8:26-bk-01234", "2026-10-30", ["1", "2"], ReceiptNumber: "R-1"),
            FollowUps = ["pay the fee"],
        };
        Assert.True(await Connect().Filings.ClaimAsync(filing, Ct));
        Assert.False(await Connect().Filings.ClaimAsync(filing with { AttemptId = "attempt-2" }, Ct));
        Assert.Equal(filing, await Connect().Filings.GetAsync(owner.Id, filing.FilingId, Ct));
        Assert.Null(await Connect().Filings.GetAsync(owner.Id, NewId(), Ct));
    }

    [Fact]
    public async Task Of_two_consumers_claiming_one_filing_exactly_one_wins()
    {
        var owner = await CreateCaseAsync();
        var filing = MakeFiling(owner.Id);
        var claims = await Task.WhenAll(
            Connect().Filings.ClaimAsync(filing, Ct),
            Connect().Filings.ClaimAsync(filing with { AttemptId = "attempt-2" }, Ct));
        Assert.Single(claims, won => won);
    }

    [Fact]
    public async Task A_transition_needs_the_state_and_the_attempt_it_read()
    {
        var owner = await CreateCaseAsync();
        var filing = MakeFiling(owner.Id);
        await Connect().Filings.ClaimAsync(filing, Ct);
        var signedIn = Step(filing, FilingState.SignedIn);

        Assert.False(await Connect().Filings.TransitionAsync(signedIn, FilingState.Uploading, cancellationToken: Ct));
        Assert.False(await Connect().Filings.TransitionAsync(signedIn with { AttemptId = "attempt-2" }, FilingState.Claimed, cancellationToken: Ct));
        Assert.True(await Connect().Filings.TransitionAsync(signedIn, FilingState.Claimed, cancellationToken: Ct));
        Assert.False(await Connect().Filings.TransitionAsync(signedIn, FilingState.Claimed, cancellationToken: Ct));
        Assert.Equal(signedIn, await Connect().Filings.GetAsync(owner.Id, filing.FilingId, Ct));
    }

    [Fact]
    public async Task Of_two_transitions_from_one_state_exactly_one_lands()
    {
        var owner = await CreateCaseAsync();
        var filing = MakeFiling(owner.Id);
        await Connect().Filings.ClaimAsync(filing, Ct);
        var moves = await Task.WhenAll(
            Connect().Filings.TransitionAsync(Step(filing, FilingState.SignedIn), FilingState.Claimed, cancellationToken: Ct),
            Connect().Filings.TransitionAsync(Step(filing, FilingState.HandedBack), FilingState.Claimed, cancellationToken: Ct));
        Assert.Single(moves, won => won);
    }

    [Fact]
    public async Task Filing_the_record_files_the_case_and_writes_its_event_in_the_same_write()
    {
        var (owner, filing) = await SubmittedAsync(chapter: 13);
        var write = Filed(owner, await Connect().Cases.LifecycleAsync(owner.Id, Ct));
        Assert.True(await Connect().Filings.TransitionAsync(Step(filing, FilingState.Filed), FilingState.Submitted, write, Ct));

        var stored = await Connect().Cases.ReadForWorkerAsync(owner.Id, Ct);
        Assert.Equal((CaseStatus.Filed, "6:26-bk-10000", new DateOnly(2026, 10, 30)), (stored!.Status, stored.CaseNumber, stored.FiledAt!.Value));
        var recorded = (await Connect().Cases.LifecycleAsync(owner.Id, Ct))[^1];
        Assert.Equal(write.Event, recorded);
        Assert.Equal((13, filing.FilingId, LifecycleProvenance.FilingWorker), (recorded.Chapter!.Value, recorded.FilingId, recorded.Provenance));
        Assert.Equal(FilingState.Filed, (await Connect().Filings.GetAsync(owner.Id, filing.FilingId, Ct))!.State);
    }

    [Fact]
    public async Task The_case_write_moves_only_the_lifecycle_and_keeps_every_other_edit()
    {
        var (owner, filing) = await SubmittedAsync();
        var write = Filed(owner, await Connect().Cases.LifecycleAsync(owner.Id, Ct));
        await Connect().Cases.UpdateAsync(owner with { Meeting341At = new DateOnly(2026, 12, 1), Judge = "Hon. Example" }, CaseStatus.ReadyToFile, Ct);

        Assert.True(await Connect().Filings.TransitionAsync(Step(filing, FilingState.Filed), FilingState.Submitted, write, Ct));
        var stored = await Connect().Cases.ReadForWorkerAsync(owner.Id, Ct);
        Assert.Equal((new DateOnly(2026, 12, 1), "Hon. Example", CaseStatus.Filed), (stored!.Meeting341At!.Value, stored.Judge, stored.Status));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("chapter")]
    [InlineData("history")]
    [InlineData("deleted")]
    public async Task A_case_that_moved_underneath_writes_nothing_at_all(string moved)
    {
        var (owner, filing) = await SubmittedAsync();
        var write = Filed(owner, await Connect().Cases.LifecycleAsync(owner.Id, Ct));
        var cases = Connect().Cases;
        switch (moved)
        {
            case "status":
                await cases.RecordAsync(Move(owner, CaseStatus.Intake, await cases.LifecycleAsync(owner.Id, Ct)), Ct);
                break;
            case "chapter":
                await cases.UpdateAsync(owner with { Chapter = 13 }, CaseStatus.ReadyToFile, Ct);
                break;
            case "history":
                // Back and forth: the status is the one read, the history is not.
                var back = Move(owner, CaseStatus.Intake, await cases.LifecycleAsync(owner.Id, Ct));
                await cases.RecordAsync(back, Ct);
                await Connect().Cases.RecordAsync(Move(back.Case, CaseStatus.ReadyToFile, await Connect().Cases.LifecycleAsync(owner.Id, Ct)), Ct);
                break;
            default:
                await cases.UpdateAsync(CaseRules.MarkDeleted(owner, Ids.Alice), CaseStatus.ReadyToFile, Ct);
                break;
        }

        var before = await Connect().Cases.LifecycleAsync(owner.Id, Ct);
        Assert.False(await Connect().Filings.TransitionAsync(Step(filing, FilingState.Filed), FilingState.Submitted, write, Ct));
        Assert.Equal(FilingState.Submitted, (await Connect().Filings.GetAsync(owner.Id, filing.FilingId, Ct))!.State);
        Assert.NotEqual(CaseStatus.Filed, (await Connect().Cases.ReadForWorkerAsync(owner.Id, Ct))!.Status);
        Assert.Equal(before, await Connect().Cases.LifecycleAsync(owner.Id, Ct));
    }

    [Fact]
    public async Task A_filing_record_that_moved_writes_nothing_to_the_case()
    {
        var owner = await ReadyAsync();
        var filing = MakeFiling(owner.Id) with { State = FilingState.OutcomeUnknown };
        await Connect().Filings.ClaimAsync(filing, Ct);
        Assert.False(await Connect().Filings.TransitionAsync(Step(filing, FilingState.Filed), FilingState.Submitted, Filed(owner, await Connect().Cases.LifecycleAsync(owner.Id, Ct)), Ct));
        Assert.Equal(CaseStatus.ReadyToFile, (await Connect().Cases.ReadForWorkerAsync(owner.Id, Ct))!.Status);
    }

    [Fact]
    public async Task Of_two_writers_filing_one_case_from_one_read_exactly_one_lands()
    {
        var (owner, filing) = await SubmittedAsync();
        var write = Filed(owner, await Connect().Cases.LifecycleAsync(owner.Id, Ct));
        var outcomes = await Task.WhenAll(
            Connect().Filings.TransitionAsync(Step(filing, FilingState.Filed), FilingState.Submitted, write, Ct),
            Connect().Filings.TransitionAsync(Step(filing, FilingState.Filed), FilingState.Submitted, write, Ct));
        Assert.Single(outcomes, won => won);
        Assert.Single(await Connect().Cases.LifecycleAsync(owner.Id, Ct), recorded => recorded.ToStatus == CaseStatus.Filed);
    }

    [Fact]
    public async Task A_hand_back_is_resolved_once()
    {
        var owner = await ReadyAsync();
        var filing = MakeFiling(owner.Id) with { State = FilingState.HandedBack };
        await Connect().Filings.ClaimAsync(filing, Ct);
        var resolution = new Resolution(ResolutionOutcome.Filed, Ids.Alice, Claimed.AddDays(1), Claimed.AddDays(1), "6:26-bk-10000", new DateOnly(2026, 10, 30), "doc-1");
        var resolved = filing with { Resolution = resolution, UpdatedAt = Claimed.AddDays(1) };
        var write = CaseLifecycle.FileCase(owner, await Connect().Cases.LifecycleAsync(owner.Id, Ct), "6:26-bk-10000", "2026-10-30", Ids.Alice, filing.FilingId, LifecycleProvenance.Manual);

        Assert.True(await Connect().Filings.ResolveAsync(resolved, FilingState.HandedBack, write, Ct));
        Assert.Equal(CaseStatus.Filed, (await Connect().Cases.ReadForWorkerAsync(owner.Id, Ct))!.Status);
        var again = filing with { Resolution = resolution with { Outcome = ResolutionOutcome.NotFiled } };
        Assert.False(await Connect().Filings.ResolveAsync(again, FilingState.HandedBack, cancellationToken: Ct));
        Assert.Equal(resolution, (await Connect().Filings.GetAsync(owner.Id, filing.FilingId, Ct))!.Resolution);
    }

    [Fact]
    public async Task Only_a_move_of_its_own_case_into_filed_rides_on_a_filing_write()
    {
        var (owner, filing) = await SubmittedAsync();
        var back = Move(owner, CaseStatus.Intake, await Connect().Cases.LifecycleAsync(owner.Id, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Connect().Filings.TransitionAsync(Step(filing, FilingState.Filed), FilingState.Submitted, back, Ct));
        var other = await ReadyAsync();
        var elsewhere = Filed(other, await Connect().Cases.LifecycleAsync(other.Id, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Connect().Filings.TransitionAsync(Step(filing, FilingState.Filed), FilingState.Submitted, elsewhere, Ct));
    }

    // A case moved to ready_to_file through the writer, and a submitted filing of it.
    private async Task<(Case Owner, Filing Filing)> SubmittedAsync(int chapter = 7)
    {
        var owner = await ReadyAsync(chapter);
        var filing = MakeFiling(owner.Id) with { State = FilingState.Submitted };
        await Connect().Filings.ClaimAsync(filing, Ct);
        return (owner, filing);
    }

    private async Task<Case> ReadyAsync(int chapter = 7)
    {
        var created = await CreateCaseAsync(chapter: chapter);
        var ready = Move(created, CaseStatus.ReadyToFile, []);
        return (await Connect().Cases.RecordAsync(ready, Ct))!;
    }

    private static LifecycleWrite Move(Case current, CaseStatus to, IReadOnlyList<LifecycleEvent> history) =>
        CaseLifecycle.StatusMove(current, current with { Status = to, UpdatedAt = current.UpdatedAt.AddMinutes(1) }, history, Ids.Alice)!;

    private static LifecycleWrite Filed(Case current, IReadOnlyList<LifecycleEvent> history) =>
        CaseLifecycle.FileCase(current, history, "6:26-bk-10000", "2026-10-30", CaseRules.FilingWorkerActor, "filing-of-" + current.Id);

    private static Filing MakeFiling(string caseId) =>
        new("filing-of-" + caseId, caseId, NewId(), Ids.Firm, Ids.Alice, NewId(), "flmb", "8", FilingState.Claimed, AttemptId, Claimed, 4_071_989_400, Claimed)
        {
            History = [new FilingStep(FilingState.Claimed, Claimed)],
        };

    private static Filing Step(Filing filing, FilingState state) =>
        filing with { State = state, UpdatedAt = Claimed.AddMinutes(5), History = [.. filing.History, new FilingStep(state, Claimed.AddMinutes(5))] };
}

/// <summary>The oracle: <see cref="Core.Memory.MemoryFilingStore"/>.</summary>
public sealed class MemoryFilingStoreConformance : FilingStoreConformance
{
    private readonly MemoryCaseWorld _world = new();

    private protected override CaseStores Connect() => _world.Connect();
}

/// <summary><see cref="Cases.PostgresFilingStore"/> on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresFilingStoreConformance(PostgresFixture postgres) : FilingStoreConformance
{
    private readonly PostgresCaseWorld _world = new(postgres);

    public override async ValueTask InitializeAsync()
    {
        await _world.InitializeAsync();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _world.DisposeAsync();
        await base.DisposeAsync();
    }

    private protected override CaseStores Connect() => _world.Connect();
}
