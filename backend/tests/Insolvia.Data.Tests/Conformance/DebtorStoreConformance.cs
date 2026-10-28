using Insolvia.Core;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;
using Insolvia.Core.Provenance;
using Insolvia.Core.TaxIds;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What every <see cref="Core.Ports.IDebtorStore"/> must do: once-only create,
/// the link's three refusals in Python's order (role_taken, client_taken,
/// client_unavailable), the merge's repoint, and the race of one client
/// linked to two roles at once.
/// </summary>
public abstract class DebtorStoreConformance : CaseConformance
{
    [Fact]
    public async Task A_role_is_created_once_and_the_first_id_stays()
    {
        var owner = await CreateCaseAsync();
        var first = MakeDebtor(owner);
        Assert.True(await Connect().Debtors.CreateAsync(first, Ct));
        Assert.False(await Connect().Debtors.CreateAsync(MakeDebtor(owner, email: "other@example.test"), Ct));
        Assert.Equal(first, await Connect().Debtors.GetAsync(owner.Id, FilingRole.Debtor1, Ct));
    }

    [Fact]
    public async Task A_whole_debtor_round_trips_body_provenance_tax_id_and_link()
    {
        var client = await ClientAsync();
        var owner = await CreateCaseAsync();
        var confirmed = new ProvenanceEntry(ProvenanceSource.AiExtracted) { ConfirmedBy = Ids.Alice, ConfirmedAt = "2026-10-02T00:00:00.000000Z", DocumentId = "doc-1" };
        var debtor = MakeDebtor(owner, clientId: client.Id) with
        {
            Body = DebtorBody.Empty with
            {
                Name = new PersonName("Jordan", "Q", "Example"),
                ResidenceAddress = new Address("1 Example St", City: "Tampa", State: "FL", PostalCode: "33601", County: "Hillsborough"),
                EmployerIds = ["12-3456789"],
                SignedAt = new DateOnly(2026, 10, 3),
                Venue = new Venue(VenueBasis.LivedLongest180Days),
            },
            Provenance = new Dictionary<string, ProvenanceEntry>
            {
                ["name.given"] = new(ProvenanceSource.StaffTyped),
                ["name.surname"] = confirmed,
                ["name.middle"] = new(ProvenanceSource.StaffTyped),
                ["residence_address.line1"] = new(ProvenanceSource.StaffTyped),
            }.ToValueDictionary(),
            TaxId = new TaxIdRef(TaxIdKind.Ssn, "4321", "ref-1"),
        };
        await Connect().Debtors.PutAsync(debtor, Ct);
        Assert.Equal(debtor, await Connect().Debtors.GetAsync(owner.Id, FilingRole.Debtor1, Ct));
    }

    [Fact]
    public async Task Put_replaces_and_the_roles_list_in_print_order()
    {
        var owner = await CreateCaseAsync();
        await Connect().Debtors.PutAsync(MakeDebtor(owner, FilingRole.NonFilingSpouse), Ct);
        await Connect().Debtors.PutAsync(MakeDebtor(owner, FilingRole.Debtor2), Ct);
        await Connect().Debtors.PutAsync(MakeDebtor(owner, FilingRole.Debtor1), Ct);
        var replaced = MakeDebtor(owner, FilingRole.Debtor2, email: "new@example.test");
        await Connect().Debtors.PutAsync(replaced, Ct);

        var listed = await Connect().Debtors.ListForCaseAsync(owner.Id, Ct);
        Assert.Equal([FilingRole.Debtor1, FilingRole.Debtor2, FilingRole.NonFilingSpouse], listed.Select(debtor => debtor.FilingRole));
        Assert.Equal(replaced, listed[1]);
        Assert.Equal(
            [new DebtorClientLink(owner.Id, FilingRole.Debtor1, null), new DebtorClientLink(owner.Id, FilingRole.Debtor2, null), new DebtorClientLink(owner.Id, FilingRole.NonFilingSpouse, null)],
            await Connect().Debtors.ClientLinksForCaseAsync(owner.Id, Ct));
    }

    [Fact]
    public async Task A_link_writes_when_every_condition_holds()
    {
        var client = await ClientAsync();
        var owner = await CreateCaseAsync();
        var linked = MakeDebtor(owner, clientId: client.Id);
        Assert.Equal(LinkOutcome.Written, await Connect().Debtors.LinkAsync(linked, create: true, Ids.Firm, Ct));
        Assert.Equal(linked, await Connect().Debtors.GetAsync(owner.Id, FilingRole.Debtor1, Ct));
        Assert.Equal([(owner.Id, FilingRole.Debtor1)], await Connect().Debtors.RolesForClientAsync(client.Id, Ct));
    }

    [Fact]
    public async Task A_create_link_on_a_filled_role_is_role_taken_before_anything_else()
    {
        var client = await ClientAsync();
        var owner = await CreateCaseAsync();
        await Connect().Debtors.LinkAsync(MakeDebtor(owner, FilingRole.Debtor2, client.Id), create: true, Ids.Firm, Ct);
        var held = MakeDebtor(owner);
        await Connect().Debtors.PutAsync(held, Ct);
        await ArchiveAsync(client);

        // All three conditions fail; the role answers first.
        Assert.Equal(LinkOutcome.RoleTaken, await Connect().Debtors.LinkAsync(MakeDebtor(owner, clientId: client.Id), create: true, Ids.Firm, Ct));
        Assert.Equal(held, await Connect().Debtors.GetAsync(owner.Id, FilingRole.Debtor1, Ct));
    }

    [Fact]
    public async Task A_client_on_another_role_is_client_taken_before_unavailable()
    {
        var client = await ClientAsync();
        var owner = await CreateCaseAsync();
        await Connect().Debtors.LinkAsync(MakeDebtor(owner, FilingRole.Debtor2, client.Id), create: true, Ids.Firm, Ct);
        await ArchiveAsync(client);

        Assert.Equal(LinkOutcome.ClientTaken, await Connect().Debtors.LinkAsync(MakeDebtor(owner, clientId: client.Id), create: false, Ids.Firm, Ct));
        Assert.Null(await Connect().Debtors.GetAsync(owner.Id, FilingRole.Debtor1, Ct));
    }

    [Fact]
    public async Task A_client_no_longer_linkable_is_client_unavailable_and_nothing_is_written()
    {
        var client = await ClientAsync();
        await ArchiveAsync(client);
        var owner = await CreateCaseAsync();
        Assert.Equal(LinkOutcome.ClientUnavailable, await Connect().Debtors.LinkAsync(MakeDebtor(owner, clientId: client.Id), create: true, Ids.Firm, Ct));
        Assert.Empty(await Connect().Debtors.ListForCaseAsync(owner.Id, Ct));

        var theirs = await ClientAsync(Ids.OtherFirm);
        Assert.Equal(LinkOutcome.ClientUnavailable, await Connect().Debtors.LinkAsync(MakeDebtor(owner, clientId: theirs.Id), create: true, Ids.Firm, Ct));
        Assert.Empty(await Connect().Debtors.ListForCaseAsync(owner.Id, Ct));
    }

    [Fact]
    public async Task A_client_merging_away_refuses_a_link_and_its_survivor_accepts_one()
    {
        var (merged, survivor) = (await ClientAsync(surname: "Merged"), await ClientAsync(surname: "Survivor"));
        Assert.True(await Connect().Firms.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));
        var owner = await CreateCaseAsync();
        Assert.Equal(LinkOutcome.ClientUnavailable, await Connect().Debtors.LinkAsync(MakeDebtor(owner, clientId: merged.Id), create: true, Ids.Firm, Ct));
        Assert.Equal(LinkOutcome.Written, await Connect().Debtors.LinkAsync(MakeDebtor(owner, clientId: survivor.Id), create: true, Ids.Firm, Ct));
    }

    [Fact]
    public async Task One_client_linked_to_two_roles_at_once_lands_on_exactly_one()
    {
        var client = await ClientAsync();
        var owner = await CreateCaseAsync();
        var outcomes = await Task.WhenAll(
            Connect().Debtors.LinkAsync(MakeDebtor(owner, FilingRole.Debtor1, client.Id), create: true, Ids.Firm, Ct),
            Connect().Debtors.LinkAsync(MakeDebtor(owner, FilingRole.Debtor2, client.Id), create: true, Ids.Firm, Ct));

        Assert.Equal([LinkOutcome.Written, LinkOutcome.ClientTaken], outcomes.Order());
        Assert.Single(await Connect().Debtors.RolesForClientAsync(client.Id, Ct));
    }

    [Fact]
    public async Task The_by_client_index_lists_every_role_sorted_by_case_then_role()
    {
        var client = await ClientAsync();
        var first = await CreateCaseAsync(caseId: "00000000-0000-4000-8000-0000000000c1");
        var second = await CreateCaseAsync(caseId: "00000000-0000-4000-8000-0000000000c2");
        await Connect().Debtors.LinkAsync(MakeDebtor(second, FilingRole.Debtor2, client.Id), create: true, Ids.Firm, Ct);
        await Connect().Debtors.LinkAsync(MakeDebtor(first, FilingRole.Debtor1, client.Id), create: true, Ids.Firm, Ct);
        Assert.Equal([(first.Id, FilingRole.Debtor1), (second.Id, FilingRole.Debtor2)], await Connect().Debtors.RolesForClientAsync(client.Id, Ct));
    }

    [Fact]
    public async Task A_repoint_moves_only_the_link()
    {
        var (merged, survivor) = (await ClientAsync(surname: "Merged"), await ClientAsync(surname: "Survivor"));
        var owner = await CreateCaseAsync();
        var before = MakeDebtor(owner, clientId: merged.Id);
        await Connect().Debtors.LinkAsync(before, create: true, Ids.Firm, Ct);

        Assert.Equal(RepointOutcome.Written, await Connect().Debtors.RepointClientAsync(owner.Id, FilingRole.Debtor1, merged.Id, survivor.Id, Ct));
        var after = await Connect().Debtors.GetAsync(owner.Id, FilingRole.Debtor1, Ct);
        Assert.Equal((survivor.Id, owner.CreatedAt), (after!.ClientId, after.CaseCreatedAt!.Value));
        Assert.Equal((before.Id, before.Body, before.Provenance), (after.Id, after.Body, after.Provenance));
        Assert.Empty(await Connect().Debtors.RolesForClientAsync(merged.Id, Ct));
    }

    [Fact]
    public async Task A_repoint_of_a_role_that_no_longer_names_the_merged_client_is_absent()
    {
        var (merged, survivor, other) = (await ClientAsync(surname: "Merged"), await ClientAsync(surname: "Survivor"), await ClientAsync(surname: "Other"));
        var owner = await CreateCaseAsync();
        await Connect().Debtors.LinkAsync(MakeDebtor(owner, clientId: other.Id), create: true, Ids.Firm, Ct);
        Assert.Equal(RepointOutcome.Absent, await Connect().Debtors.RepointClientAsync(owner.Id, FilingRole.Debtor1, merged.Id, survivor.Id, Ct));
        Assert.Equal(RepointOutcome.Absent, await Connect().Debtors.RepointClientAsync(owner.Id, FilingRole.Debtor2, merged.Id, survivor.Id, Ct));
        Assert.Equal(other.Id, (await Connect().Debtors.GetAsync(owner.Id, FilingRole.Debtor1, Ct))!.ClientId);
    }

    [Fact]
    public async Task A_repoint_onto_a_survivor_holding_another_role_is_client_taken()
    {
        var (merged, survivor) = (await ClientAsync(surname: "Merged"), await ClientAsync(surname: "Survivor"));
        var owner = await CreateCaseAsync();
        await Connect().Debtors.LinkAsync(MakeDebtor(owner, FilingRole.Debtor1, merged.Id), create: true, Ids.Firm, Ct);
        await Connect().Debtors.LinkAsync(MakeDebtor(owner, FilingRole.Debtor2, survivor.Id), create: true, Ids.Firm, Ct);
        Assert.Equal(RepointOutcome.ClientTaken, await Connect().Debtors.RepointClientAsync(owner.Id, FilingRole.Debtor1, merged.Id, survivor.Id, Ct));
        Assert.Equal(merged.Id, (await Connect().Debtors.GetAsync(owner.Id, FilingRole.Debtor1, Ct))!.ClientId);
    }
}

/// <summary>The oracle: <see cref="Core.Memory.MemoryDebtorStore"/>.</summary>
public sealed class MemoryDebtorStoreConformance : DebtorStoreConformance
{
    private readonly MemoryCaseWorld _world = new();

    private protected override CaseStores Connect() => _world.Connect();
}

/// <summary><see cref="Cases.PostgresDebtorStore"/> on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresDebtorStoreConformance(PostgresFixture postgres) : DebtorStoreConformance
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
