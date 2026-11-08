using Insolvia.Core;
using Insolvia.Core.Filings;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What every <see cref="Core.Ports.IFilingApprovalStore"/> must do (ADR 0024,
/// guardrail 1): one live approval per case through the pointer, a pending
/// one superseded in the same write, single use (pending, same digest,
/// unexpired) — and of two racing consumers, or two racing approvals from one
/// read, exactly one wins.
/// </summary>
public abstract class FilingApprovalStoreConformance : CaseConformance
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const long Now = 4_071_988_800;
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(Now);

    [Fact]
    public async Task An_approval_is_created_current_and_reads_back_whole()
    {
        var owner = await CreateCaseAsync();
        var approval = Make(owner.Id);

        Assert.Null(await Connect().Approvals.CurrentAsync(owner.Id, Ct));
        await Connect().Approvals.CreateAsync(approval, null, At, Ct);

        Assert.Equal(approval, await Connect().Approvals.CurrentAsync(owner.Id, Ct));
        Assert.Equal(approval, await Connect().Approvals.GetAsync(owner.Id, approval.ApprovalId, Ct));
        Assert.Null(await Connect().Approvals.GetAsync(owner.Id, NewId(), Ct));
    }

    [Fact]
    public async Task A_new_approval_supersedes_the_pending_one_in_the_same_write()
    {
        var owner = await CreateCaseAsync();
        var first = Make(owner.Id);
        await Connect().Approvals.CreateAsync(first, null, At, Ct);
        var second = Make(owner.Id);

        await Connect().Approvals.CreateAsync(second, first, At.AddSeconds(5), Ct);

        Assert.Equal(second.ApprovalId, (await Connect().Approvals.CurrentAsync(owner.Id, Ct))!.ApprovalId);
        var old = (await Connect().Approvals.GetAsync(owner.Id, first.ApprovalId, Ct))!;
        Assert.Equal((ApprovalStatus.Voided, VoidReasons.Superseded, At.AddSeconds(5)), (old.Status, old.VoidReason, old.VoidedAt!.Value));
    }

    [Fact]
    public async Task A_create_past_a_moved_pointer_is_a_conflict_and_writes_nothing()
    {
        var owner = await CreateCaseAsync();
        var first = Make(owner.Id);
        await Connect().Approvals.CreateAsync(first, null, At, Ct);
        var stale = Make(owner.Id);

        await Assert.ThrowsAsync<ConflictException>(() => Connect().Approvals.CreateAsync(stale, null, At, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => Connect().Approvals.CreateAsync(stale, Make(owner.Id), At, Ct));

        Assert.Null(await Connect().Approvals.GetAsync(owner.Id, stale.ApprovalId, Ct));
        Assert.Equal(first, await Connect().Approvals.CurrentAsync(owner.Id, Ct));
    }

    [Fact]
    public async Task Two_approvals_raced_from_one_read_land_exactly_one()
    {
        var owner = await CreateCaseAsync();
        var first = Make(owner.Id);
        await Connect().Approvals.CreateAsync(first, null, At, Ct);

        var won = await RaceAsync<ConflictException>(
            () => Connect().Approvals.CreateAsync(Make(owner.Id), first, At, Ct),
            () => Connect().Approvals.CreateAsync(Make(owner.Id), first, At, Ct));

        Assert.Single(won, success => success);
    }

    [Fact]
    public async Task Consume_is_single_use_and_needs_pending_the_digest_and_no_expiry()
    {
        var owner = await CreateCaseAsync();
        var approval = Make(owner.Id);
        await Connect().Approvals.CreateAsync(approval, null, At, Ct);

        Assert.False(await Connect().Approvals.ConsumeAsync(owner.Id, approval.ApprovalId, new string('b', 64), Now, At, Ct));
        Assert.False(await Connect().Approvals.ConsumeAsync(owner.Id, approval.ApprovalId, Digest, approval.ExpiresAt, At, Ct));
        Assert.True(await Connect().Approvals.ConsumeAsync(owner.Id, approval.ApprovalId, Digest, Now, At, Ct));
        Assert.False(await Connect().Approvals.ConsumeAsync(owner.Id, approval.ApprovalId, Digest, Now, At, Ct));

        var stored = (await Connect().Approvals.GetAsync(owner.Id, approval.ApprovalId, Ct))!;
        Assert.Equal((ApprovalStatus.Consumed, At), (stored.Status, stored.ConsumedAt!.Value));
        Assert.False(await Connect().Approvals.VoidAsync(owner.Id, approval.ApprovalId, VoidReasons.Cancelled, At, Ct));
    }

    [Fact]
    public async Task Of_two_consumers_racing_one_approval_exactly_one_consumes()
    {
        var owner = await CreateCaseAsync();
        var approval = Make(owner.Id);
        await Connect().Approvals.CreateAsync(approval, null, At, Ct);

        var consumed = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await Task.Yield();
            return await Connect().Approvals.ConsumeAsync(owner.Id, approval.ApprovalId, Digest, Now, At, Ct);
        }));

        Assert.Single(consumed, won => won);
    }

    [Fact]
    public async Task A_voided_approval_cannot_be_consumed()
    {
        var owner = await CreateCaseAsync();
        var approval = Make(owner.Id);
        await Connect().Approvals.CreateAsync(approval, null, At, Ct);

        Assert.True(await Connect().Approvals.VoidAsync(owner.Id, approval.ApprovalId, VoidReasons.Changed, At, Ct));
        Assert.False(await Connect().Approvals.VoidAsync(owner.Id, approval.ApprovalId, VoidReasons.Changed, At, Ct));
        Assert.False(await Connect().Approvals.ConsumeAsync(owner.Id, approval.ApprovalId, Digest, Now, At, Ct));
        Assert.Equal(VoidReasons.Changed, (await Connect().Approvals.GetAsync(owner.Id, approval.ApprovalId, Ct))!.VoidReason);
    }

    private static FilingApproval Make(string caseId) =>
        new(NewId(), NewId(), caseId, Ids.Firm, Ids.Alice, NewId(), NewId(), Digest, NewId(), "flmb", "8", "2026-10-01.1", At, Now - 10, Now + 3600);
}

/// <summary>The oracle: <see cref="Core.Memory.MemoryFilingApprovalStore"/>.</summary>
public sealed class MemoryFilingApprovalStoreConformance : FilingApprovalStoreConformance
{
    private readonly MemoryCaseWorld _world = new();

    private protected override CaseStores Connect() => _world.Connect();
}

/// <summary><see cref="Cases.PostgresFilingApprovalStore"/> on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresFilingApprovalStoreConformance(PostgresFixture postgres) : FilingApprovalStoreConformance
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
