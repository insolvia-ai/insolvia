using Insolvia.Core.Debtors;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Tests;

/// <summary>
/// The debtor store's merge-facing half, in memory — what Python's
/// <c>MemoryDebtorStore</c> answered for <c>roles_for_client</c> and
/// <c>repoint_client</c>, with the same two conditions checked in one step.
/// The full debtor store (and its memory twin) is 18.7's; it implements
/// <see cref="IDebtorClientLinks"/> and this fake goes.
/// </summary>
internal sealed class FakeDebtorLinks : IDebtorClientLinks
{
    private readonly Dictionary<(string CaseId, FilingRole Role), string?> _links = [];
    private int _cases;

    /// <summary>Runs inside a repoint, before its conditions are checked — a write racing the merge.</summary>
    public Action<string, FilingRole>? BeforeRepoint { get; set; }

    public string CaseFor(params string[] clientIds)
    {
        var caseId = $"case-{++_cases}";
        FilingRole[] roles = [FilingRole.Debtor1, FilingRole.Debtor2];
        for (var index = 0; index < clientIds.Length; index++)
        {
            _links[(caseId, roles[index])] = clientIds[index];
        }

        return caseId;
    }

    public void Link(string caseId, FilingRole role, string? clientId) => _links[(caseId, role)] = clientId;

    public string? ClientOf(string caseId, FilingRole role) => _links.GetValueOrDefault((caseId, role));

    public Task<IReadOnlyList<(string CaseId, FilingRole FilingRole)>> RolesForClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(string, FilingRole)> roles = [.. _links
            .Where(pair => pair.Value == clientId)
            .Select(pair => pair.Key)
            .OrderBy(key => key.CaseId, StringComparer.Ordinal)
            .ThenBy(key => key.Role)];
        return Task.FromResult(roles);
    }

    public Task<RepointOutcome> RepointClientAsync(string caseId, FilingRole filingRole, string fromClientId, string toClientId, CancellationToken cancellationToken = default)
    {
        BeforeRepoint?.Invoke(caseId, filingRole);
        if (_links.GetValueOrDefault((caseId, filingRole)) != fromClientId)
        {
            return Task.FromResult(RepointOutcome.Absent);
        }

        if (_links.Any(pair => pair.Key.CaseId == caseId && pair.Key.Role != filingRole && pair.Value == toClientId))
        {
            return Task.FromResult(RepointOutcome.ClientTaken);
        }

        _links[(caseId, filingRole)] = toClientId;
        return Task.FromResult(RepointOutcome.Written);
    }

    public Task<IReadOnlyList<DebtorClientLink>> ClientLinksForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DebtorClientLink> links = [.. _links
            .Where(pair => pair.Key.CaseId == caseId)
            .Select(pair => new DebtorClientLink(caseId, pair.Key.Role, pair.Value))];
        return Task.FromResult(links);
    }
}

/// <summary>A firm store whose merge finish can be made to race itself.</summary>
internal sealed class RacingFirmStore(MemoryFirmStore inner) : IFirmStore
{
    public bool FinishTwice { get; set; }

    public Task CreateFirmAsync(Insolvia.Core.Firms.Firm firm, CancellationToken cancellationToken = default) => inner.CreateFirmAsync(firm, cancellationToken);

    public Task<Insolvia.Core.Firms.Firm?> GetFirmAsync(string firmId, CancellationToken cancellationToken = default) => inner.GetFirmAsync(firmId, cancellationToken);

    public Task<IReadOnlyList<Insolvia.Core.Firms.Firm>> ListFirmsAsync(CancellationToken cancellationToken = default) => inner.ListFirmsAsync(cancellationToken);

    public Task<Insolvia.Core.Firms.Firm?> UpdateFirmAsync(Insolvia.Core.Firms.Firm firm, CancellationToken cancellationToken = default) => inner.UpdateFirmAsync(firm, cancellationToken);

    public Task AddUserAsync(Insolvia.Core.Firms.FirmUser user, CancellationToken cancellationToken = default) => inner.AddUserAsync(user, cancellationToken);

    public Task<Insolvia.Core.Firms.FirmUser?> GetUserAsync(string firmId, string subject, CancellationToken cancellationToken = default) => inner.GetUserAsync(firmId, subject, cancellationToken);

    public Task<Insolvia.Core.Firms.FirmUser?> FindUserAsync(string subject, CancellationToken cancellationToken = default) => inner.FindUserAsync(subject, cancellationToken);

    public Task<IReadOnlyList<Insolvia.Core.Firms.FirmUser>> ListUsersAsync(string firmId, CancellationToken cancellationToken = default) => inner.ListUsersAsync(firmId, cancellationToken);

    public Task<Insolvia.Core.Firms.FirmUser?> UpdateUserAsync(Insolvia.Core.Firms.FirmUser user, CancellationToken cancellationToken = default) => inner.UpdateUserAsync(user, cancellationToken);

    public Task<bool> RemoveUserAsync(string firmId, string subject, CancellationToken cancellationToken = default) => inner.RemoveUserAsync(firmId, subject, cancellationToken);

    public Task CreateLibraryCreditorAsync(LibraryCreditors.LibraryCreditor creditor, CancellationToken cancellationToken = default) => inner.CreateLibraryCreditorAsync(creditor, cancellationToken);

    public Task<LibraryCreditors.LibraryCreditor?> GetLibraryCreditorAsync(string firmId, string creditorId, CancellationToken cancellationToken = default) => inner.GetLibraryCreditorAsync(firmId, creditorId, cancellationToken);

    public Task<IReadOnlyList<LibraryCreditors.LibraryCreditor>> ListLibraryCreditorsAsync(string firmId, CancellationToken cancellationToken = default) => inner.ListLibraryCreditorsAsync(firmId, cancellationToken);

    public Task<LibraryCreditors.LibraryCreditor?> UpdateLibraryCreditorAsync(LibraryCreditors.LibraryCreditor creditor, CancellationToken cancellationToken = default) => inner.UpdateLibraryCreditorAsync(creditor, cancellationToken);

    public Task<bool> DeleteLibraryCreditorAsync(string firmId, string creditorId, CancellationToken cancellationToken = default) => inner.DeleteLibraryCreditorAsync(firmId, creditorId, cancellationToken);

    public Task CreateClientAsync(FirmClient client, CancellationToken cancellationToken = default) => inner.CreateClientAsync(client, cancellationToken);

    public Task<FirmClient?> GetClientAsync(string firmId, string clientId, CancellationToken cancellationToken = default) => inner.GetClientAsync(firmId, clientId, cancellationToken);

    public Task<IReadOnlyList<FirmClient>> ListClientsAsync(string firmId, CancellationToken cancellationToken = default) => inner.ListClientsAsync(firmId, cancellationToken);

    public Task<FirmClient?> UpdateClientAsync(FirmClient client, CancellationToken cancellationToken = default) => inner.UpdateClientAsync(client, cancellationToken);

    public Task<FirmClient?> SetClientProspectStageAsync(string firmId, string clientId, ProspectStage? stage, CancellationToken cancellationToken = default) => inner.SetClientProspectStageAsync(firmId, clientId, stage, cancellationToken);

    public Task<FirmClient?> MarkClientRetainedAsync(string firmId, string clientId, DateOnly retainedOn, CancellationToken cancellationToken = default) => inner.MarkClientRetainedAsync(firmId, clientId, retainedOn, cancellationToken);

    public Task<bool> ClaimClientMergeAsync(string firmId, string mergedId, string survivorId, CancellationToken cancellationToken = default) => inner.ClaimClientMergeAsync(firmId, mergedId, survivorId, cancellationToken);

    public async Task<FirmClient?> FinishClientMergeAsync(string firmId, string mergedId, string survivorId, DateTimeOffset mergedAt, CancellationToken cancellationToken = default)
    {
        if (FinishTwice)
        {
            // The other run of this same merge finishes first.
            await inner.FinishClientMergeAsync(firmId, mergedId, survivorId, mergedAt, cancellationToken);
        }

        return await inner.FinishClientMergeAsync(firmId, mergedId, survivorId, mergedAt, cancellationToken);
    }

    public Task ReleaseClientMergeAsync(string firmId, string mergedId, string survivorId, CancellationToken cancellationToken = default) => inner.ReleaseClientMergeAsync(firmId, mergedId, survivorId, cancellationToken);

    public Task<Questionnaires.QuestionnaireConfig?> GetQuestionnaireAsync(string firmId, CancellationToken cancellationToken = default) => inner.GetQuestionnaireAsync(firmId, cancellationToken);

    public Task PutQuestionnaireAsync(Questionnaires.QuestionnaireConfig config, CancellationToken cancellationToken = default) => inner.PutQuestionnaireAsync(config, cancellationToken);

    public Task<bool> DeleteQuestionnaireAsync(string firmId, CancellationToken cancellationToken = default) => inner.DeleteQuestionnaireAsync(firmId, cancellationToken);

    public Task<DocumentRequests.DocumentChecklist?> GetDocumentChecklistAsync(string firmId, CancellationToken cancellationToken = default) => inner.GetDocumentChecklistAsync(firmId, cancellationToken);

    public Task PutDocumentChecklistAsync(DocumentRequests.DocumentChecklist checklist, CancellationToken cancellationToken = default) => inner.PutDocumentChecklistAsync(checklist, cancellationToken);

    public Task<bool> DeleteDocumentChecklistAsync(string firmId, CancellationToken cancellationToken = default) => inner.DeleteDocumentChecklistAsync(firmId, cancellationToken);
}

// Translated from test_client_merge.py: merging two firm clients (ADR 0022's
// PR 7). The cases are the fake's (the case and debtor stores are 18.7's);
// what the merge does to the CLIENT rows and to each case's link is the same.
public sealed class ClientMergeTests
{
    private readonly MemoryFirmStore _firms = new();
    private readonly FakeDebtorLinks _debtors = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<FirmClient> Client(string given, string surname = "Example")
    {
        var client = FirmClientRules.Create(
            FirmClientRules.Parse(Body.Of($$$"""{"name": {"given": "{{{given}}}", "surname": "{{{surname}}}"}, "phone": "555-0100", "residence_address": {"line1": "1 Example St"}}""")),
            Ids.Firm,
            Ids.Alice);
        await _firms.CreateClientAsync(client, Ct);
        return client;
    }

    private async Task<FirmClient> Get(FirmClient client) => (await _firms.GetClientAsync(Ids.Firm, client.Id, Ct))!;

    private async Task<MergeResult> Merge(FirmClient merged, FirmClient survivor, IFirmStore? store = null) =>
        await ClientMerges.MergeAsync(store ?? _firms, _debtors, await Get(survivor), await Get(merged), cancellationToken: Ct);

    // ── What a merge does ───────────────────────────────────────────

    [Fact]
    public async Task Every_case_of_the_merged_client_is_repointed_to_the_survivor()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordan", "Exampel");
        var first = _debtors.CaseFor(dupe.Id);
        var second = _debtors.CaseFor(dupe.Id);

        await Merge(dupe, keep);

        Assert.Empty(await _debtors.RolesForClientAsync(dupe.Id, Ct));
        Assert.Equal([(first, FilingRole.Debtor1), (second, FilingRole.Debtor1)], await _debtors.RolesForClientAsync(keep.Id, Ct));
    }

    [Fact]
    public async Task The_merged_client_is_archived_pointing_at_the_survivor()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");

        var result = await Merge(dupe, keep);

        var stored = await Get(dupe);
        Assert.Equal(FirmClientStatus.Archived, stored.Status);
        Assert.Equal(keep.Id, stored.MergedInto);
        Assert.Equal(stored, result.Merged);
        Assert.Equal(keep.Id, result.Survivor.Id);
        Assert.Equal(FirmClientStatus.Active, (await Get(keep)).Status);
    }

    [Fact]
    public async Task A_client_with_no_cases_merges()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        Assert.Equal(keep.Id, (await Merge(dupe, keep)).Merged.MergedInto);
    }

    [Fact]
    public async Task The_claim_is_released_on_both_rows()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        await Merge(dupe, keep);
        Assert.Null((await Get(keep)).MergingFrom);
        Assert.Null((await Get(dupe)).MergingInto);
    }

    // ── What a merge refuses ────────────────────────────────────────

    [Fact]
    public async Task A_client_cannot_be_merged_into_itself()
    {
        var one = await Client("Jordan");
        var refused = await Assert.ThrowsAsync<FieldValidationException>(() => Merge(one, one));
        Assert.Contains("merged_client_id", refused.Fields.Keys);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("reverse")]
    [InlineData("onward")]
    public async Task A_merged_client_cannot_be_merged_again(string direction)
    {
        var keep = await Client("A");
        var dupe = await Client("B");
        var third = await Client("C");
        await Merge(dupe, keep);
        var (merged, survivor) = direction switch
        {
            "same" => (dupe, keep),
            "reverse" => (keep, dupe),
            _ => (dupe, third),
        };
        await Assert.ThrowsAsync<ConflictException>(() => Merge(merged, survivor));
    }

    [Fact]
    public async Task A_merged_client_cannot_be_opened_for_a_new_case()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        await Merge(dupe, keep);
        Assert.NotNull((await Get(dupe)).RefusalForNewCase());
        Assert.Null((await Get(keep)).RefusalForNewCase());
    }

    [Fact]
    public async Task An_archived_client_is_refused_either_way()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        await _firms.UpdateClientAsync((await Get(dupe)) with { Status = FirmClientStatus.Archived }, Ct);
        await Assert.ThrowsAsync<ConflictException>(() => Merge(dupe, keep));
        await Assert.ThrowsAsync<ConflictException>(() => Merge(keep, dupe));
    }

    [Fact]
    public async Task Two_clients_on_one_joint_case_are_refused_and_nothing_moves()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        var joint = _debtors.CaseFor(keep.Id, dupe.Id);
        var alone = _debtors.CaseFor(dupe.Id);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => Merge(dupe, keep));
        Assert.Contains("both debtors", refused.Message, StringComparison.Ordinal);

        Assert.Equal([(joint, FilingRole.Debtor2), (alone, FilingRole.Debtor1)], await _debtors.RolesForClientAsync(dupe.Id, Ct));
        Assert.Null((await Get(dupe)).MergingInto);
        Assert.Null((await Get(keep)).MergingFrom);
    }

    [Fact]
    public async Task A_joint_case_that_appears_mid_merge_releases_the_claim()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        var caseId = _debtors.CaseFor(dupe.Id);
        _debtors.BeforeRepoint = (repointCase, _) => _debtors.Link(repointCase, FilingRole.Debtor2, keep.Id);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => Merge(dupe, keep));
        Assert.Contains("both debtors", refused.Message, StringComparison.Ordinal);

        Assert.Equal(FirmClientStatus.Active, (await Get(dupe)).Status);
        Assert.Null((await Get(dupe)).MergingInto);
        Assert.Null((await Get(keep)).MergingFrom);
        Assert.Equal(dupe.Id, _debtors.ClientOf(caseId, FilingRole.Debtor1));
    }

    // ── Merges into and out of the same client at once ──────────────

    [Theory]
    [InlineData("keep", "third")]
    [InlineData("third", "dupe")]
    [InlineData("third", "keep")]
    [InlineData("dupe", "third")]
    public async Task While_a_merge_is_claimed_no_overlapping_merge_can_claim(string merged, string survivor)
    {
        var clients = new Dictionary<string, FirmClient>
        {
            ["keep"] = await Client("A"),
            ["dupe"] = await Client("B"),
            ["third"] = await Client("C"),
        };
        Assert.True(await _firms.ClaimClientMergeAsync(Ids.Firm, clients["dupe"].Id, clients["keep"].Id, Ct));
        var refused = await Assert.ThrowsAsync<ConflictException>(() => Merge(clients[merged], clients[survivor]));
        Assert.Contains("in progress", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_merge_that_stopped_half_way_finishes_when_run_again()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        var caseId = _debtors.CaseFor(dupe.Id);
        await _firms.ClaimClientMergeAsync(Ids.Firm, dupe.Id, keep.Id, Ct);

        await Merge(dupe, keep);

        Assert.Equal(keep.Id, (await Get(dupe)).MergedInto);
        Assert.Equal(keep.Id, _debtors.ClientOf(caseId, FilingRole.Debtor1));
    }

    [Fact]
    public async Task A_client_being_merged_away_is_not_opened_for_a_case()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        await _firms.ClaimClientMergeAsync(Ids.Firm, dupe.Id, keep.Id, Ct);
        Assert.Equal("That client is being merged into another client.", (await Get(dupe)).RefusalForNewCase());
        Assert.Null((await Get(keep)).RefusalForNewCase());
    }

    [Fact]
    public async Task An_edit_read_before_the_claim_does_not_erase_it()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        var stale = await Get(keep);
        await _firms.ClaimClientMergeAsync(Ids.Firm, dupe.Id, keep.Id, Ct);

        await _firms.UpdateClientAsync(stale with { Phone = "555-0199" }, Ct);

        Assert.Equal(dupe.Id, (await Get(keep)).MergingFrom);
    }

    [Fact]
    public async Task A_merged_client_cannot_be_edited_back_to_life()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        var stale = await Get(dupe);
        await Merge(dupe, keep);
        Assert.Null(await _firms.UpdateClientAsync(stale with { Status = FirmClientStatus.Active }, Ct));
        Assert.Equal(FirmClientStatus.Archived, (await Get(dupe)).Status);
    }

    [Fact]
    public async Task An_unclaimed_finish_changes_nothing()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        Assert.Null(await _firms.FinishClientMergeAsync(Ids.Firm, dupe.Id, keep.Id, DateTimeOffset.UnixEpoch, Ct));
        Assert.Equal(FirmClientStatus.Active, (await Get(dupe)).Status);
    }

    [Fact]
    public async Task A_concurrent_duplicate_of_the_same_merge_answers_what_it_left()
    {
        var keep = await Client("Jordan");
        var dupe = await Client("Jordy");
        var racing = new RacingFirmStore(_firms) { FinishTwice = true };
        var result = await Merge(dupe, keep, racing);
        Assert.Equal(keep.Id, result.Merged.MergedInto);
    }

    [Fact]
    public void The_refusal_pre_check_names_each_state()
    {
        var keep = FirmClientTests.Make();
        var dupe = FirmClientTests.Make();
        Assert.Null(ClientMerges.Refusal(keep, dupe));
        Assert.Equal("That client was already merged into another client.", ClientMerges.Refusal(keep, dupe with { MergedInto = "x" }));
        Assert.Equal("This client was merged into another client and cannot receive a merge.", ClientMerges.Refusal(keep with { MergedInto = "x" }, dupe));
        Assert.Equal(ClientMerges.InProgress, ClientMerges.Refusal(keep, dupe with { MergingFrom = "x" }));
        Assert.Null(ClientMerges.Refusal(keep with { MergingFrom = dupe.Id }, dupe with { MergingInto = keep.Id }));
    }
}
