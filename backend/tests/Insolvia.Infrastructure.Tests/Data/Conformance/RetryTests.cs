using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Creditors;
using Insolvia.Core.Fields;
using Insolvia.Core.Filings;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Jobs;
using Insolvia.Core.Provenance;
using Insolvia.Data.AccessLogging;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// The stores under EF Core's retrying execution strategy (#567): a transient
/// failure — a refused connection while a paused Aurora resumes, a dropped
/// socket — is replayed and the operation succeeds; and a write whose commit
/// landed but whose reply was lost is neither applied twice nor reported to
/// the caller as a conflict it caused itself. Faults come from
/// <see cref="FaultInjector"/>, one per plan, on a real Postgres.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class RetryTests(PostgresFixture postgres) : CaseConformance
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const long Now = 4_071_988_800;
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(Now);
    private static readonly DateTimeOffset Claimed = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresCaseWorld _world = new(postgres);
    private readonly FaultInjector _faults = new();

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

    // ── A transient failure is retried ──────────────────────────────

    [Fact]
    public async Task A_read_whose_connection_is_refused_is_retried_and_answers()
    {
        _faults.Plan(Fault.RefuseConnection);

        Assert.NotNull(await Connect().Firms.GetFirmAsync(Ids.Firm, Ct));
        Assert.Single(_faults.Injected);
    }

    [Fact]
    public async Task A_transaction_that_fails_midway_is_replayed_whole()
    {
        var created = Make.Case(NewId());
        _faults.Plan(Fault.FailCommand, "INSERT INTO insolvia.case_assignments");

        await Connect().Cases.CreateAsync(created, Make.Assignment(created), cancellationToken: Ct);

        Assert.Single(_faults.Injected);
        Assert.NotNull(await Plain().Cases.ReadForWorkerAsync(created.Id, Ct));
        Assert.Single(await Plain().Cases.AssigneesAsync(created.Id, Ct));
    }

    [Fact]
    public async Task A_conditional_update_that_fails_is_retried_and_lands()
    {
        var job = await JobAsync();
        var running = JobRules.StartAttempt(job);
        _faults.Plan(Fault.FailCommand, "UPDATE insolvia.jobs");

        Assert.NotNull(await Connect().Jobs.UpdateAsync(running, JobStatus.Queued, Ct));

        Assert.Single(_faults.Injected);
        Assert.Equal(JobStatus.Running, (await Plain().Jobs.GetAsync(job.CaseId, job.Id, Ct))!.Status);
    }

    [Fact]
    public async Task An_append_only_insert_that_fails_is_retried_and_recorded_once()
    {
        var accessEvent = AccessEvents.Record(Ids.Alice, AccessAction.CaseRead, caseId: Ids.Case);
        _faults.Plan(Fault.FailCommand, "INSERT INTO insolvia.access_log");

        await new PostgresAccessLog(_world.Harness.NewContext(_faults)).RecordAsync(accessEvent, Ct);

        Assert.Single(_faults.Injected);
        Assert.Equal(1, await AccessRowsAsync(accessEvent.EventId));
    }

    // ── A lost reply is neither applied twice nor a spurious refusal ─

    [Fact]
    public async Task An_access_event_whose_reply_is_lost_is_recorded_once_without_an_error()
    {
        var accessEvent = AccessEvents.Record(Ids.Alice, AccessAction.CaseRead, caseId: Ids.Case);
        _faults.Plan(Fault.LoseCommandAck, "INSERT INTO insolvia.access_log");

        await new PostgresAccessLog(_world.Harness.NewContext(_faults)).RecordAsync(accessEvent, Ct);

        Assert.Equal(["LoseCommandAck: reply lost (committed)"], _faults.Injected);
        Assert.Equal(1, await AccessRowsAsync(accessEvent.EventId));
    }

    [Fact]
    public async Task An_event_id_recorded_twice_without_a_fault_is_still_refused()
    {
        var accessEvent = AccessEvents.Record(Ids.Alice, AccessAction.CaseRead, caseId: Ids.Case);
        var log = new PostgresAccessLog(_world.Harness.NewContext(_faults));
        await log.RecordAsync(accessEvent, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresAccessLog(_world.Harness.NewContext(_faults)).RecordAsync(accessEvent, Ct));
        Assert.Equal(1, await AccessRowsAsync(accessEvent.EventId));
    }

    [Fact]
    public async Task A_transaction_whose_commit_reply_is_lost_reports_success_and_writes_once()
    {
        var created = Make.Case(NewId());
        _faults.Plan(Fault.LoseCommitAck);

        await Connect().Cases.CreateAsync(created, Make.Assignment(created), cancellationToken: Ct);

        Assert.Single(_faults.Injected);
        Assert.NotNull(await Plain().Cases.ReadForWorkerAsync(created.Id, Ct));
        Assert.Single(await Plain().Cases.AssigneesAsync(created.Id, Ct));
    }

    [Fact]
    public async Task A_case_copy_whose_commit_reply_is_lost_reports_success_and_writes_once()
    {
        var copy = Make.Case(NewId());
        var creditor = Creditor(copy.Id);
        var stores = Connect();
        _faults.Plan(Fault.LoseCommitAck);

        // The copy route's unit: the case, then a record the foreign key ties
        // to it. The case store's own landed check is skipped inside a joined
        // transaction, so the unit carries it.
        await stores.Work.RunAsync(
            async token =>
            {
                await stores.Cases.CreateAsync(copy, Make.Assignment(copy), cancellationToken: token);
                await stores.Entities.CreateAsync(creditor, token);
            },
            landed: async token => await stores.Cases.ReadForWorkerAsync(copy.Id, token) is not null,
            Ct);

        Assert.Single(_faults.Injected);
        Assert.Equal([creditor.Id], (await Plain().Entities.ListForCaseAsync(copy.Id, CreditorRules.Kind, Ct)).Select(entity => entity.Id));
    }

    [Fact]
    public async Task A_case_copy_whose_commit_reply_is_lost_reads_as_its_own_conflict_without_the_check()
    {
        var copy = Make.Case(NewId());
        var stores = Connect();
        _faults.Plan(Fault.LoseCommitAck);

        // Why the copy route passes landed: the replay meets its own case row.
        await Assert.ThrowsAsync<InvalidOperationException>(() => stores.Work.RunAsync(
            token => stores.Cases.CreateAsync(copy, Make.Assignment(copy), cancellationToken: token),
            cancellationToken: Ct));

        Assert.NotNull(await Plain().Cases.ReadForWorkerAsync(copy.Id, Ct));
    }

    [Fact]
    public async Task A_lifecycle_event_whose_commit_reply_is_lost_is_recorded_once_and_is_no_conflict()
    {
        var owner = await CreateCaseAsync();
        var ready = CaseLifecycle.StatusMove(owner, owner with { Status = CaseStatus.ReadyToFile, UpdatedAt = owner.UpdatedAt.AddMinutes(1) }, [], Ids.Alice)!;
        _faults.Plan(Fault.LoseCommitAck);

        Assert.Equal(ready.Case, await Connect().Cases.RecordAsync(ready, Ct));

        Assert.Single(_faults.Injected);
        Assert.Equal([ready.Event], await Plain().Cases.LifecycleAsync(owner.Id, Ct));
    }

    [Fact]
    public async Task A_conditional_update_whose_reply_is_lost_reports_the_write_it_made()
    {
        var job = await JobAsync();
        var running = JobRules.StartAttempt(job);
        _faults.Plan(Fault.LoseCommandAck, "UPDATE insolvia.jobs");

        Assert.NotNull(await Connect().Jobs.UpdateAsync(running, JobStatus.Queued, Ct));

        Assert.Equal(["LoseCommandAck: reply lost (committed)"], _faults.Injected);
        var stored = await Plain().Jobs.GetAsync(job.CaseId, job.Id, Ct);
        Assert.Equal((JobStatus.Running, 1), (stored!.Status, stored.Attempts));
    }

    [Fact]
    public async Task An_approval_consume_whose_reply_is_lost_succeeds_and_stays_single_use()
    {
        var owner = await CreateCaseAsync();
        var approval = Approval(owner.Id);
        await Plain().Approvals.CreateAsync(approval, null, At, Ct);
        _faults.Plan(Fault.LoseCommandAck, "UPDATE insolvia.filing_approvals");

        Assert.True(await Connect().Approvals.ConsumeAsync(owner.Id, approval.ApprovalId, Digest, Now, At, Ct));

        Assert.Single(_faults.Injected);
        Assert.False(await Plain().Approvals.ConsumeAsync(owner.Id, approval.ApprovalId, Digest, Now, At.AddSeconds(1), Ct));
    }

    [Fact]
    public async Task A_retried_consume_never_reads_a_rivals_consume_as_its_own()
    {
        var owner = await CreateCaseAsync();
        var approval = Approval(owner.Id);
        await Plain().Approvals.CreateAsync(approval, null, At, Ct);
        Assert.True(await Plain().Approvals.ConsumeAsync(owner.Id, approval.ApprovalId, Digest, Now, At, Ct));
        _faults.Plan(Fault.FailCommand, "UPDATE insolvia.filing_approvals");

        Assert.False(await Connect().Approvals.ConsumeAsync(owner.Id, approval.ApprovalId, Digest, Now, At.AddSeconds(1), Ct));
        Assert.Single(_faults.Injected);
    }

    [Fact]
    public async Task A_filing_claim_whose_reply_is_lost_is_won_and_a_rival_still_loses()
    {
        var owner = await CreateCaseAsync();
        var filing = MakeFiling(owner.Id);
        _faults.Plan(Fault.LoseCommandAck, "INSERT INTO insolvia.filings");

        Assert.True(await Connect().Filings.ClaimAsync(filing, Ct));

        Assert.Equal(["LoseCommandAck: reply lost (committed)"], _faults.Injected);
        Assert.False(await Plain().Filings.ClaimAsync(filing with { AttemptId = "attempt-2" }, Ct));
    }

    [Fact]
    public async Task A_retried_claim_never_reads_a_rivals_claim_as_its_own()
    {
        var owner = await CreateCaseAsync();
        var filing = MakeFiling(owner.Id);
        Assert.True(await Plain().Filings.ClaimAsync(filing with { AttemptId = "attempt-2" }, Ct));
        _faults.Plan(Fault.FailCommand, "INSERT INTO insolvia.filings");

        Assert.False(await Connect().Filings.ClaimAsync(filing, Ct));
        Assert.Single(_faults.Injected);
        Assert.Equal("attempt-2", (await Plain().Filings.GetAsync(owner.Id, filing.FilingId, Ct))!.AttemptId);
    }

    [Fact]
    public async Task A_filing_transition_whose_commit_reply_is_lost_reports_the_move_it_made()
    {
        var owner = await CreateCaseAsync();
        var filing = MakeFiling(owner.Id);
        await Plain().Filings.ClaimAsync(filing, Ct);
        var signedIn = filing with { State = FilingState.SignedIn, UpdatedAt = Claimed.AddMinutes(5), History = [.. filing.History, new FilingStep(FilingState.SignedIn, Claimed.AddMinutes(5))] };
        _faults.Plan(Fault.LoseCommitAck);

        Assert.True(await Connect().Filings.TransitionAsync(signedIn, FilingState.Claimed, cancellationToken: Ct));

        Assert.Single(_faults.Injected);
        Assert.Equal(signedIn, await Plain().Filings.GetAsync(owner.Id, filing.FilingId, Ct));
    }

    [Fact]
    public async Task A_client_merge_whose_commit_reply_is_lost_reports_the_archived_client()
    {
        var merged = await ClientAsync(surname: "Merged");
        var survivor = await ClientAsync(surname: "Survivor");
        Assert.True(await Plain().Firms.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));
        _faults.Plan(Fault.LoseCommitAck);

        var archived = await Connect().Firms.FinishClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Claimed, Ct);

        Assert.Single(_faults.Injected);
        Assert.NotNull(archived);
        Assert.Equal((FirmClientStatus.Archived, survivor.Id), (archived.Status, archived.MergedInto));
    }

    [Fact]
    public async Task A_delete_whose_reply_is_lost_reports_the_row_it_removed()
    {
        await Plain().Firms.AddUserAsync(Make.User(Ids.Bob), Ct);
        _faults.Plan(Fault.LoseCommandAck, "DELETE FROM insolvia.firm_users");

        Assert.True(await Connect().Firms.RemoveUserAsync(Ids.Firm, Ids.Bob, Ct));

        Assert.Equal(["LoseCommandAck: reply lost (committed)"], _faults.Injected);
        Assert.Null(await Plain().Firms.GetUserAsync(Ids.Firm, Ids.Bob, Ct));
    }

    // Through the fault injector: what the test arms applies here.
    private protected override CaseStores Connect() => _world.Connect(_faults);

    // Around it: set-up and the reads that check the outcome.
    private CaseStores Plain() => _world.Connect();

    private async Task<Job> JobAsync()
    {
        var owner = await CreateCaseAsync();
        var job = new Job(NewId(), owner.Id, JobRules.EchoKind, JobStatus.Queued, Ids.Alice, Opened, Opened);
        await Plain().Jobs.CreateAsync(job, Ct);
        return job;
    }

    private async Task<int> AccessRowsAsync(string eventId) =>
        await _world.Harness.NewContext().Set<AccessLogRow>().AsNoTracking().CountAsync(row => row.EventId == eventId, Ct);

    private static CaseEntity<CreditorBody> Creditor(string caseId) =>
        new(
            CreditorRules.Kind,
            NewId(),
            caseId,
            Opened,
            Opened,
            new CreditorBody("Example Bank", new Address("1 Bank Plaza", City: "Tampa", State: "FL", PostalCode: "33602")),
            new Dictionary<string, ProvenanceEntry> { ["name"] = new(ProvenanceSource.StaffTyped) }.ToValueDictionary());

    private static FilingApproval Approval(string caseId) =>
        new(NewId(), NewId(), caseId, Ids.Firm, Ids.Alice, NewId(), NewId(), Digest, NewId(), "flmb", "8", "2026-10-01.1", At, Now - 10, Now + 3600);

    private static Filing MakeFiling(string caseId) =>
        new("filing-of-" + caseId, caseId, NewId(), Ids.Firm, Ids.Alice, NewId(), "flmb", "8", FilingState.Claimed, "attempt-1", Claimed, 4_071_989_400, Claimed)
        {
            History = [new FilingStep(FilingState.Claimed, Claimed)],
        };
}
