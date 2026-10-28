using Insolvia.Core;
using Insolvia.Core.Clients;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data.Portal;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What every <see cref="IClientBindingStore"/> must do, against the memory
/// twin and the Postgres store. Ported from the memory half of
/// <c>test_client_binding_store.py</c> (<c>MemoryStoreTests.cs</c>), plus a
/// case for each condition of the bind transaction and the update.
/// </summary>
public abstract class ClientBindingStoreConformance : IAsyncLifetime
{
    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Another handle on the SAME store.</summary>
    private protected abstract IClientBindingStore Connect();

    /// <summary>Who holds <paramref name="role"/> on <paramref name="caseId"/> — the claims are not on the port.</summary>
    private protected abstract Task<string?> ClaimHolderAsync(string caseId, ClientRole role);

    private static ClientBinding Binding(string subject = Ids.Pat, ClientRole[]? roles = null, BindingStatus status = BindingStatus.Invited, string firmId = Ids.Firm, string caseId = Ids.Case) =>
        Make.Binding(subject, roles, status, caseId, firmId);

    [Fact]
    public async Task Two_bindings_cannot_both_hold_debtor_2()
    {
        var store = Connect();
        await store.BindAsync(Binding(Ids.Pat, [ClientRole.Debtor2]), [], Ct);
        await Assert.ThrowsAsync<ConflictException>(() => store.BindAsync(Binding(Ids.Sam, [ClientRole.Debtor2]), [], Ct));
        Assert.Equal(Ids.Pat, await ClaimHolderAsync(Ids.Case, ClientRole.Debtor2));
        Assert.Null(await store.GetAsync(Ids.Firm, Ids.Sam, Ct));
        Assert.Equal([Ids.Pat], (await store.ListForCaseAsync(Ids.Case, Ct)).Select(b => b.Subject));
    }

    [Fact]
    public async Task A_narrowing_moves_the_role_and_rewrites_the_first_binding()
    {
        var store = Connect();
        var first = Binding(Ids.Pat, [ClientRole.Debtor1, ClientRole.Debtor2], BindingStatus.Active);
        await store.BindAsync(first, [], Ct);
        await store.BindAsync(Binding(Ids.Sam, [ClientRole.Debtor2]), [first with { Roles = [ClientRole.Debtor1] }], Ct);
        Assert.Equal(Ids.Pat, await ClaimHolderAsync(Ids.Case, ClientRole.Debtor1));
        Assert.Equal(Ids.Sam, await ClaimHolderAsync(Ids.Case, ClientRole.Debtor2));
        Assert.Equal([ClientRole.Debtor1], (await Connect().GetAsync(Ids.Firm, Ids.Pat, Ct))!.Roles);
        Assert.Equal([Ids.Pat, Ids.Sam], (await store.ListForCaseAsync(Ids.Case, Ct)).Select(b => b.Subject));
        Assert.Equal([ClientRole.Debtor1], (await store.ListForCaseAsync(Ids.Case, Ct))[0].Roles);
    }

    [Fact]
    public async Task A_live_subject_cannot_be_bound_twice_but_a_revoked_one_can()
    {
        var store = Connect();
        await store.BindAsync(Binding(), [], Ct);
        await Assert.ThrowsAsync<ConflictException>(() => store.BindAsync(Binding(), [], Ct));
        await store.UpdateAsync(ClientBindings.Revoke(Binding()), Ct);
        await store.BindAsync(Binding(), [], Ct);
        Assert.Equal(BindingStatus.Invited, (await store.GetAsync(Ids.Firm, Ids.Pat, Ct))!.Status);
    }

    [Fact]
    public async Task An_active_subject_cannot_be_bound_to_another_case()
    {
        var store = Connect();
        await store.BindAsync(Binding(status: BindingStatus.Active), [], Ct);
        await Assert.ThrowsAsync<ConflictException>(() => store.BindAsync(Binding(caseId: Ids.OtherCase), [], Ct));
        Assert.Empty(await store.ListForCaseAsync(Ids.OtherCase, Ct));
        Assert.Null(await ClaimHolderAsync(Ids.OtherCase, ClientRole.Debtor1));
    }

    [Fact]
    public async Task A_revoked_subject_rebinds_to_a_refiled_case_and_the_old_case_keeps_its_record()
    {
        var store = Connect();
        await store.BindAsync(Binding(), [], Ct);
        await store.UpdateAsync(ClientBindings.Revoke(Binding()), Ct);
        await store.BindAsync(Binding(caseId: Ids.OtherCase), [], Ct);

        Assert.Equal(Ids.OtherCase, (await store.GetAsync(Ids.Firm, Ids.Pat, Ct))!.CaseId);
        Assert.Equal([BindingStatus.Revoked], (await store.ListForCaseAsync(Ids.Case, Ct)).Select(b => b.Status));
        Assert.Equal([BindingStatus.Invited], (await store.ListForCaseAsync(Ids.OtherCase, Ct)).Select(b => b.Status));
        Assert.Null(await ClaimHolderAsync(Ids.Case, ClientRole.Debtor1));
        Assert.Equal(Ids.Pat, await ClaimHolderAsync(Ids.OtherCase, ClientRole.Debtor1));
    }

    [Fact]
    public async Task Revoking_releases_the_role_for_someone_else()
    {
        var store = Connect();
        await store.BindAsync(Binding(Ids.Pat, [ClientRole.Debtor2]), [], Ct);
        await store.UpdateAsync(ClientBindings.Revoke(Binding(Ids.Pat, [ClientRole.Debtor2])), Ct);
        Assert.Null(await ClaimHolderAsync(Ids.Case, ClientRole.Debtor2));
        await store.BindAsync(Binding(Ids.Sam, [ClientRole.Debtor2]), [], Ct);
        Assert.Equal(Ids.Sam, await ClaimHolderAsync(Ids.Case, ClientRole.Debtor2));
    }

    [Fact]
    public async Task Revoking_releases_only_the_claims_this_subject_holds()
    {
        // A revocation from a stale read names a role the subject no longer
        // holds: the other holder's claim stays.
        var store = Connect();
        var first = Binding(Ids.Pat, [ClientRole.Debtor1, ClientRole.Debtor2], BindingStatus.Active);
        await store.BindAsync(first, [], Ct);
        await store.BindAsync(Binding(Ids.Sam, [ClientRole.Debtor2]), [first with { Roles = [ClientRole.Debtor1] }], Ct);

        await store.UpdateAsync(ClientBindings.Revoke(first), Ct);

        Assert.Null(await ClaimHolderAsync(Ids.Case, ClientRole.Debtor1));
        Assert.Equal(Ids.Sam, await ClaimHolderAsync(Ids.Case, ClientRole.Debtor2));
    }

    [Fact]
    public async Task An_update_rewrites_the_binding_and_its_mirror()
    {
        var store = Connect();
        await store.BindAsync(Binding(), [], Ct);
        var activated = Binding(status: BindingStatus.Active) with { UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        Assert.Equal(activated, await store.UpdateAsync(activated, Ct));
        Assert.Equal(activated, await Connect().GetAsync(Ids.Firm, Ids.Pat, Ct));
        Assert.Equal([activated], await store.ListForCaseAsync(Ids.Case, Ct));
        Assert.Equal(Ids.Pat, await ClaimHolderAsync(Ids.Case, ClientRole.Debtor1));
    }

    [Fact]
    public async Task Updating_a_binding_that_does_not_exist_returns_none_and_writes_nothing()
    {
        var store = Connect();
        Assert.Null(await store.UpdateAsync(Binding(status: BindingStatus.Revoked), Ct));
        Assert.Null(await store.UpdateAsync(Binding(status: BindingStatus.Active), Ct));
        Assert.Null(await store.GetAsync(Ids.Firm, Ids.Pat, Ct));
        Assert.Empty(await store.ListForCaseAsync(Ids.Case, Ct));
    }

    [Fact]
    public async Task Updating_across_firms_finds_no_binding()
    {
        var store = Connect();
        await store.BindAsync(Binding(), [], Ct);
        Assert.Null(await store.UpdateAsync(Binding(firmId: Ids.OtherFirm, status: BindingStatus.Revoked), Ct));
        Assert.Equal(BindingStatus.Invited, (await store.GetAsync(Ids.Firm, Ids.Pat, Ct))!.Status);
        Assert.Equal(Ids.Pat, await ClaimHolderAsync(Ids.Case, ClientRole.Debtor1));
    }

    [Fact]
    public async Task A_subject_resolves_to_its_one_binding()
    {
        var store = Connect();
        await store.BindAsync(Binding(), [], Ct);
        Assert.Equal(Binding(), await store.FindAsync(Ids.Pat, Ct));
        Assert.Null(await store.FindAsync(Ids.Sam, Ct));
    }

    [Fact]
    public async Task A_subject_in_two_firms_raises_rather_than_choosing()
    {
        var store = Connect();
        await store.BindAsync(Binding(firmId: Ids.Firm), [], Ct);
        await store.BindAsync(Binding(firmId: Ids.OtherFirm, caseId: Ids.OtherCase), [], Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindAsync(Ids.Pat, Ct));
    }

    [Fact]
    public async Task The_email_lookup_is_firm_scoped_and_case_insensitive()
    {
        var store = Connect();
        await store.BindAsync(Binding(firmId: Ids.OtherFirm), [], Ct);
        Assert.Null(await store.FindByEmailAsync(Ids.Firm, "a1@example.test", Ct));
        Assert.NotNull(await store.FindByEmailAsync(Ids.OtherFirm, "A1@example.test", Ct));
    }

    [Fact]
    public async Task A_case_lists_every_binding_oldest_first()
    {
        var store = Connect();
        await store.BindAsync(Make.Binding(Ids.Sam, [ClientRole.Debtor2], createdAt: DateTimeOffset.UnixEpoch.AddDays(2)), [], Ct);
        await store.BindAsync(Make.Binding(Ids.Pat, [ClientRole.Debtor1], createdAt: DateTimeOffset.UnixEpoch.AddDays(1)), [], Ct);
        await store.BindAsync(Make.Binding(Ids.Kim, [ClientRole.Debtor1], caseId: Ids.OtherCase), [], Ct);
        Assert.Equal([Ids.Pat, Ids.Sam], (await store.ListForCaseAsync(Ids.Case, Ct)).Select(b => b.Subject));
    }

    [Fact]
    public async Task A_refused_bind_writes_nothing_at_all()
    {
        // The narrowed binding vanished between the plan and the write: the
        // whole transaction is refused, the new binding included.
        var store = Connect();
        await Assert.ThrowsAsync<ConflictException>(() => store.BindAsync(Binding(Ids.Sam, [ClientRole.Debtor2]), [Binding(Ids.Pat, [ClientRole.Debtor1])], Ct));
        Assert.Null(await store.GetAsync(Ids.Firm, Ids.Sam, Ct));
        Assert.Empty(await store.ListForCaseAsync(Ids.Case, Ct));
        Assert.Null(await ClaimHolderAsync(Ids.Case, ClientRole.Debtor2));
    }

    [Fact]
    public async Task A_narrowing_of_a_binding_revoked_since_the_plan_is_refused_and_writes_nothing()
    {
        var store = Connect();
        var first = Binding(Ids.Pat, [ClientRole.Debtor1, ClientRole.Debtor2], BindingStatus.Active);
        await store.BindAsync(first, [], Ct);
        await store.UpdateAsync(ClientBindings.Revoke(first), Ct);

        await Assert.ThrowsAsync<ConflictException>(() =>
            store.BindAsync(Binding(Ids.Sam, [ClientRole.Debtor2]), [first with { Roles = [ClientRole.Debtor1] }], Ct));

        Assert.Null(await store.GetAsync(Ids.Firm, Ids.Sam, Ct));
        Assert.Null(await ClaimHolderAsync(Ids.Case, ClientRole.Debtor2));
        var stillRevoked = await store.GetAsync(Ids.Firm, Ids.Pat, Ct);
        Assert.Equal((BindingStatus.Revoked, 2), (stillRevoked!.Status, stillRevoked.Roles.Count));
    }

    [Fact]
    public async Task A_role_held_by_someone_not_narrowed_refuses_the_whole_bind()
    {
        var store = Connect();
        await store.BindAsync(Binding(Ids.Pat, [ClientRole.Debtor1]), [], Ct);
        await store.BindAsync(Binding(Ids.Kim, [ClientRole.Debtor2]), [], Ct);

        // Sam asks for both roles and nobody is narrowed: Pat holds one, Kim the other.
        await Assert.ThrowsAsync<ConflictException>(() =>
            store.BindAsync(Binding(Ids.Sam, [ClientRole.Debtor1, ClientRole.Debtor2]), [], Ct));

        Assert.Null(await store.GetAsync(Ids.Firm, Ids.Sam, Ct));
        Assert.Equal(Ids.Pat, await ClaimHolderAsync(Ids.Case, ClientRole.Debtor1));
        Assert.Equal(Ids.Kim, await ClaimHolderAsync(Ids.Case, ClientRole.Debtor2));
    }

    [Fact]
    public async Task Two_racing_invitations_for_one_role_let_exactly_one_through()
    {
        var results = await Task.WhenAll(
            Attempt(() => Connect().BindAsync(Binding(Ids.Pat, [ClientRole.Debtor1]), [], Ct)),
            Attempt(() => Connect().BindAsync(Binding(Ids.Sam, [ClientRole.Debtor1]), [], Ct)));

        Assert.Single(results, won => won);
        var holder = await ClaimHolderAsync(Ids.Case, ClientRole.Debtor1);
        Assert.Equal([holder], (await Connect().ListForCaseAsync(Ids.Case, Ct)).Select(b => b.Subject));

        static async Task<bool> Attempt(Func<Task> bind)
        {
            try
            {
                await bind();
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }
    }
}

/// <summary>The oracle: <see cref="MemoryClientBindingStore"/> against its own suite.</summary>
public sealed class MemoryClientBindingStoreConformance : ClientBindingStoreConformance
{
    private readonly MemoryClientBindingStore _store = new();

    private protected override IClientBindingStore Connect() => _store;

    private protected override Task<string?> ClaimHolderAsync(string caseId, ClientRole role) =>
        Task.FromResult(_store.Claims.GetValueOrDefault((caseId, role)));
}

/// <summary><see cref="PostgresClientBindingStore"/>, held to the oracle's suite on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresClientBindingStoreConformance(PostgresFixture postgres) : ClientBindingStoreConformance
{
    private readonly PostgresHarness _harness = new(postgres);

    public override async ValueTask InitializeAsync()
    {
        await _harness.InitializeAsync();
        await _harness.SeedFirmsAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await base.DisposeAsync();
    }

    private protected override IClientBindingStore Connect() => new PostgresClientBindingStore(_harness.NewContext());

    private protected override async Task<string?> ClaimHolderAsync(string caseId, ClientRole role) =>
        (await _harness.NewContext().Set<CaseClientRoleClaimRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(claim => claim.CaseId == caseId && claim.Role == role, Ct))?.Subject;
}
