using System.Text.Json.Nodes;
using Insolvia.Core.Access;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Clients;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.Memory;
using Insolvia.Core.Provenance;
using Insolvia.Core.TaxIds;
using static Insolvia.Core.Tests.CaseFixtures;

namespace Insolvia.Core.Tests;

/// <summary>
/// A client's identity copied onto a case and back (ADR 0022), translated from
/// test_client_link.py and test_client_acts.py.
/// </summary>
public sealed class ClientCopyTests
{
    private const string FirmB = "00000000-0000-4000-8000-00000000f18b";
    private const string Dana = "00000000-0000-4000-8000-00000000da4a";

    private const string ClientBody = """
        {"name": {"given": "Jordan", "surname": "Example", "suffix": "Jr."},
         "other_names_used": [{"id": "alias-1", "surname": "Sample"}],
         "date_of_birth": "1980-02-29",
         "residence_address": {"line1": "1 Example St", "city": "Springfield", "state": "IL", "postal_code": "62701", "county": "Sangamon"},
         "phone": "555-0100", "email": "jordan@example.test", "lead_source": "Referral"}
        """;

    private static readonly ProvenanceEntry Typed = new(ProvenanceSource.StaffTyped);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FirmClient AClient(string firmId = Ids.Firm, Action<JsonObject>? overrides = null)
    {
        var body = Body.Of(ClientBody);
        overrides?.Invoke(body);
        return FirmClientRules.Create(FirmClientRules.Parse(body), firmId, Ids.Alice);
    }

    private static (Case Case, CaseAssignment Assignment) ACase(string firmId = Ids.Firm, string createdBy = Ids.Alice, TimeProvider? clock = null) =>
        CaseRules.Create(CaseRules.ParseCreation(Body.Of("""{"chapter": 7, "court": "flmb", "division": "tampa"}"""), requireClients: false), firmId, createdBy, clock);

    private static Debtor ACopy(FirmClient? client = null, FilingRole role = FilingRole.Debtor1) =>
        ClientCopies.DebtorFromClient(client ?? AClient(), ACase().Case, role);

    /// <summary>The debtor as the questionnaire would send it back, with changes and extra provenance.</summary>
    private static DebtorDraft AsSent(Debtor debtor, Action<JsonObject>? changes = null, JsonObject? provenance = null)
    {
        var body = BodyJson.Prune(DebtorRules.BodyNode(debtor.Body));
        changes?.Invoke(body);
        var entries = ProvenanceRules.ToJson(debtor.Provenance);
        foreach (var (path, entry) in provenance ?? [])
        {
            entries[path] = entry?.DeepClone();
        }

        body["provenance"] = entries;
        return DebtorRules.Parse(body);
    }

    // ── The client source ───────────────────────────────────────────

    [Fact]
    public void Client_is_a_source_and_names_its_client()
    {
        Assert.Contains("client", Wire.Names<ProvenanceSource>());
        var entries = ProvenanceRules.Parse(Body.Of("""{"name.given": {"source": "client", "client_id": "c-1"}}"""));
        Assert.Equal("c-1", entries["name.given"].ClientId);
        Assert.True(JsonNode.DeepEquals(Body.Of("""{"name.given": {"source": "client", "client_id": "c-1"}}"""), ProvenanceRules.ToJson(entries)));
    }

    [Fact]
    public void A_client_value_needs_no_confirmation() =>
        ProvenanceRules.Parse(Body.Of("""{"name.given": {"source": "client", "client_id": "c-1"}}"""));

    [Fact]
    public void A_client_id_that_is_not_text_is_refused() =>
        Assert.Contains("provenance.name.given.client_id", Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(Body.Of("""{"name.given": {"source": "client", "client_id": 7}}"""))).Fields.Keys);

    // ── The copy ────────────────────────────────────────────────────

    [Fact]
    public void The_copy_carries_the_identity_and_every_field_says_where_it_came_from()
    {
        var client = AClient();
        var (opened, _) = ACase();
        var debtor = ClientCopies.DebtorFromClient(client, opened, FilingRole.Debtor1);

        Assert.Equal((client.Id, opened.Id, opened.CreatedAt), (debtor.ClientId, debtor.CaseId, debtor.CaseCreatedAt!.Value));
        Assert.Equal(client.Name, debtor.Body.Name);
        Assert.Equal(client.OtherNamesUsed, debtor.Body.OtherNamesUsed);
        Assert.Equal(client.ResidenceAddress, debtor.Body.ResidenceAddress);
        Assert.Equal("Sangamon", debtor.Body.ResidenceAddress.County);
        Assert.Equal(("555-0100", "jordan@example.test"), (debtor.Body.Phone, debtor.Body.Email));
        var entry = new ProvenanceEntry(ProvenanceSource.Client) { ClientId = client.Id };
        Assert.Equal(
            ["email", "name.given", "name.suffix", "name.surname", "other_names_used[alias-1].surname", "phone", "residence_address.city", "residence_address.county", "residence_address.line1", "residence_address.postal_code", "residence_address.state"],
            debtor.Provenance.Keys.Order(StringComparer.Ordinal));
        Assert.All(debtor.Provenance.Values, value => Assert.Equal(entry, value));
    }

    [Fact]
    public void The_firms_own_fields_are_never_copied()
    {
        var body = BodyJson.Prune(DebtorRules.BodyNode(ACopy().Body));
        Assert.False(body.ContainsKey("date_of_birth"));
        Assert.False(body.ContainsKey("lead_source"));
        Assert.All(body.Select(pair => pair.Key).Where(key => Body.Of(ClientBody).ContainsKey(key)), key => Assert.Contains(key, ClientCopies.CopiedFields));
    }

    [Fact]
    public void The_copy_is_a_record_the_questionnaire_can_send_back() => AsSent(ACopy());

    [Fact]
    public void A_questionnaire_save_keeps_the_link_whatever_its_body_says()
    {
        var debtor = ACopy();
        var draft = DebtorRules.Parse(Body.Of("""{"client_id": "someone-else", "name": {"given": "Typed"}, "provenance": {"name.given": {"source": "staff_typed"}}}"""));
        var saved = DebtorRules.Replace(debtor, draft);
        Assert.Equal((debtor.ClientId, debtor.CaseCreatedAt), (saved.ClientId, saved.CaseCreatedAt));
        Assert.Equal("Typed", saved.Body.Name.Given);
    }

    [Fact]
    public void Client_id_is_server_owned_so_an_echo_needs_no_provenance() =>
        DebtorRules.Parse(Body.Of("""{"client_id": "c-1"}"""));

    [Fact]
    public void A_link_and_an_index_sort_key_travel_together() =>
        Assert.Contains("together", Assert.Throws<ArgumentException>(() => DebtorRules.Create(DebtorRules.Parse([]), "k", FilingRole.Debtor1, clientId: "c-1")).Message, StringComparison.Ordinal);

    [Fact]
    public void Re_linking_changes_the_client_and_never_the_copied_fields()
    {
        var debtor = ACopy(role: FilingRole.Debtor2);
        var relinked = DebtorRules.LinkClient(debtor, "c-other", debtor.CaseCreatedAt!.Value);
        Assert.Equal("c-other", relinked.ClientId);
        Assert.Equal((debtor.Body.Name, debtor.Provenance, debtor.Id), (relinked.Body.Name, relinked.Provenance, relinked.Id));
    }

    [Fact]
    public void The_address_county_is_part_of_the_copy() =>
        Assert.Equal(new Address(Line1: "1 Example St", City: "Springfield", State: "IL", PostalCode: "62701", County: "Sangamon"), ACopy().Body.ResidenceAddress);

    // ── Divergence ──────────────────────────────────────────────────

    [Fact]
    public void A_fresh_copy_differs_in_nothing()
    {
        var client = AClient();
        Assert.Empty(ClientCopies.DiffersFromClient(ACopy(client), client));
    }

    [Fact]
    public void Divergence_is_listed_by_field_path_in_both_directions()
    {
        var client = AClient();
        var debtor = ACopy(client);
        debtor = debtor with { Body = debtor.Body with { Phone = "555-0199" } };
        client = client with { Mobile = "555-0111", ResidenceAddress = client.ResidenceAddress with { County = "Cook" } };
        Assert.Equal(["mobile", "phone", "residence_address.county"], ClientCopies.DiffersFromClient(debtor, client));
    }

    [Fact]
    public void An_alias_is_compared_by_its_id_not_its_position()
    {
        var client = AClient(overrides: body => body["other_names_used"] = Body.Value("""[{"id": "alias-1", "surname": "Sample"}, {"id": "alias-2", "surname": "Other"}]"""));
        var debtor = ACopy(client);
        Assert.Empty(ClientCopies.DiffersFromClient(debtor, client with { OtherNamesUsed = client.OtherNamesUsed.Reverse().ToValueCollection() }));
        Assert.Equal(["other_names_used[alias-2].surname"], ClientCopies.DiffersFromClient(debtor, client with { OtherNamesUsed = client.OtherNamesUsed.Take(1).ToValueCollection() }));
    }

    [Fact]
    public void The_firms_own_fields_never_count_as_divergence()
    {
        var client = AClient();
        Assert.Empty(ClientCopies.DiffersFromClient(ACopy(client), client with { LeadSource = "Web" }));
    }

    // test_differs_from_client_is_served_only_when_computed (the JSON view),
    // test_a_linked_debtor_is_an_entry_in_the_by_client_index and
    // test_an_unlinked_debtor_is_not_in_the_index (the item's GSI3 keys):
    // dropped. The index itself is ListForClientAsync, below.

    // ── The case store writes the copy with the case ────────────────

    [Fact]
    public async Task The_case_its_assignment_and_its_debtors_are_written_together()
    {
        var debtors = new MemoryDebtorStore();
        var store = new MemoryCaseStore(debtors);
        var (opened, assignment) = ACase();
        var copy = ClientCopies.DebtorFromClient(AClient(), opened, FilingRole.Debtor1);
        await store.CreateAsync(opened, assignment, [copy], Ct);
        Assert.Equal(copy, await debtors.GetAsync(opened.Id, FilingRole.Debtor1, Ct));
        Assert.Equal(opened, await store.GetAsync(opened.Id, Accessor(), Ct));
    }

    [Fact]
    public async Task A_debtor_for_another_case_refuses_the_whole_write()
    {
        var store = new MemoryCaseStore();
        var (opened, assignment) = ACase();
        var stray = ClientCopies.DebtorFromClient(AClient(), ACase().Case, FilingRole.Debtor1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(opened, assignment, [stray], Ct));
        Assert.False(store.Cases.ContainsKey(opened.Id));
    }

    [Fact]
    public async Task A_clients_cases_are_the_ones_the_caller_may_see_newest_first()
    {
        var store = new MemoryCaseStore(new MemoryDebtorStore());
        var client = AClient();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var (older, olderAssignment) = ACase(createdBy: Ids.Alice, clock: clock);
        clock.Now = clock.Now.AddSeconds(1);
        var (newer, newerAssignment) = ACase(createdBy: Dana, clock: clock);
        await store.CreateAsync(older, olderAssignment, [ClientCopies.DebtorFromClient(client, older, FilingRole.Debtor2)], Ct);
        await store.CreateAsync(newer, newerAssignment, [ClientCopies.DebtorFromClient(client, newer, FilingRole.Debtor1)], Ct);

        var admin = await store.ListForClientAsync(client.Id, Accessor(isAdmin: true), Ct);
        Assert.Equal([(newer.Id, FilingRole.Debtor1), (older.Id, FilingRole.Debtor2)], admin.Select(entry => (entry.Case.Id, entry.FilingRole)));
        Assert.Equal([newer.Id], (await store.ListForClientAsync(client.Id, Accessor(Dana), Ct)).Select(entry => entry.Case.Id));
        Assert.Empty(await store.ListForClientAsync(client.Id, Accessor(Ids.Bob, firmId: FirmB), Ct));
    }

    // The three FakeDynamoDb tests (one transaction for case, assignment and
    // debtors; the index read then per-case decision; one query for no cases)
    // are the DynamoDB adapter's; dropped.

    // ── Keep, never mint: client provenance on a questionnaire save ─

    [Theory]
    [InlineData("name.given", "\"Jordan\"")]
    [InlineData("other_names_used[alias-1].surname", "\"Sample\"")]
    [InlineData("other_names_used[no-such-row].surname", null)]
    [InlineData("residence_address.postal_code", "\"62701\"")]
    [InlineData("phone", "\"555-0100\"")]
    [InlineData("not_a_field", null)]
    public void A_path_resolves_to_the_value_it_names(string path, string? expected) =>
        Assert.True(JsonNode.DeepEquals(expected is null ? null : JsonNode.Parse(expected), ProvenanceRules.ValueAt(DebtorRules.BodyNode(ACopy().Body), path)));

    [Fact]
    public void An_echo_of_the_stored_record_keeps_every_client_entry()
    {
        var stored = ACopy();
        DebtorRules.RequireClientProvenanceKept(AsSent(stored), stored);
    }

    [Fact]
    public void A_changed_field_sent_as_typed_beside_untouched_copies_is_accepted()
    {
        var stored = ACopy();
        DebtorRules.RequireClientProvenanceKept(AsSent(stored, body => body["phone"] = "555-0199", Body.Of("""{"phone": {"source": "staff_typed"}}""")), stored);
    }

    [Fact]
    public void A_changed_field_that_still_claims_the_client_is_refused()
    {
        var stored = ACopy();
        var draft = AsSent(stored, body => body["phone"] = "555-0199");
        Assert.Equal(["provenance.phone"], Assert.Throws<FieldValidationException>(() => DebtorRules.RequireClientProvenanceKept(draft, stored)).Fields.Keys);
    }

    [Fact]
    public void A_client_entry_the_stored_record_never_had_is_refused()
    {
        var copy = ACopy();
        var stored = copy with
        {
            Body = copy.Body with { Email = null },
            Provenance = copy.Provenance.Where(pair => pair.Key != "email").ToValueDictionary(),
        };
        var draft = AsSent(stored, body => body["email"] = "new@example.test", new JsonObject { ["email"] = new JsonObject { ["source"] = "client", ["client_id"] = stored.ClientId } });
        Assert.Equal(["provenance.email"], Assert.Throws<FieldValidationException>(() => DebtorRules.RequireClientProvenanceKept(draft, stored)).Fields.Keys);
    }

    [Fact]
    public void A_client_entry_naming_another_client_is_refused()
    {
        var stored = ACopy();
        var draft = AsSent(stored, provenance: Body.Of("""{"phone": {"source": "client", "client_id": "someone"}}"""));
        Assert.Throws<FieldValidationException>(() => DebtorRules.RequireClientProvenanceKept(draft, stored));
    }

    [Fact]
    public void Nothing_stored_means_no_client_entry_can_be_kept() =>
        Assert.Throws<FieldValidationException>(() => DebtorRules.RequireClientProvenanceKept(AsSent(ACopy()), null));

    [Fact]
    public void An_entry_for_a_field_now_empty_describes_nothing_and_passes()
    {
        var stored = ACopy();
        DebtorRules.RequireClientProvenanceKept(AsSent(stored, body => body.Remove("phone")), stored);
    }

    // ── Re-copy and update-client ───────────────────────────────────

    private static Debtor Diverged(Debtor stored) => stored with
    {
        Body = stored.Body with { Phone = "555-0199", Venue = new Venue(VenueBasis.LivedLongest180Days) },
        TaxId = new TaxIdRef(TaxIdKind.Ssn, "4321", "ref-1"),
        Provenance = stored.Provenance
            .Concat(new Dictionary<string, ProvenanceEntry> { ["phone"] = Typed, ["venue.basis"] = Typed, ["tax_id"] = Typed })
            .ToValueDictionary(),
    };

    [Fact]
    public void Recopy_puts_the_clients_values_back_with_client_provenance()
    {
        var client = AClient();
        var recopied = ClientCopies.RecopyFromClient(Diverged(ACopy(client)), client);
        Assert.Equal("555-0100", recopied.Body.Phone);
        Assert.Equal(new ProvenanceEntry(ProvenanceSource.Client) { ClientId = client.Id }, recopied.Provenance["phone"]);
    }

    [Fact]
    public void Recopy_keeps_the_cases_own_answers_and_their_provenance()
    {
        var client = AClient();
        var stored = Diverged(ACopy(client));
        var recopied = ClientCopies.RecopyFromClient(stored, client);
        Assert.Equal((stored.Id, stored.CreatedAt), (recopied.Id, recopied.CreatedAt));
        Assert.Equal(stored.Body.Venue, recopied.Body.Venue);
        Assert.Equal(stored.TaxId, recopied.TaxId);
        Assert.Equal(Typed, recopied.Provenance["venue.basis"]);
        Assert.Equal(Typed, recopied.Provenance["tax_id"]);
        Assert.Equal(stored.ClientId, recopied.ClientId);
    }

    [Fact]
    public void Recopy_is_a_copy_of_the_record_not_a_merge_into_the_case()
    {
        var client = AClient(overrides: body => body.Remove("email"));
        var copy = ACopy(client);
        var stored = copy with
        {
            Body = copy.Body with { Email = "typed@example.test" },
            Provenance = copy.Provenance.Append(new KeyValuePair<string, ProvenanceEntry>("email", Typed)).ToValueDictionary(),
        };
        var recopied = ClientCopies.RecopyFromClient(stored, client);
        Assert.Null(recopied.Body.Email);
        Assert.False(recopied.Provenance.ContainsKey("email"));
    }

    [Fact]
    public void Recopy_follows_a_client_that_changed_since_the_copy()
    {
        var client = AClient();
        var stored = ACopy(client);
        Assert.Equal("Jo", ClientCopies.RecopyFromClient(stored, client with { Name = new PersonName(Given: "Jo", Surname: "Example") }).Body.Name.Given);
    }

    [Fact]
    public void Update_client_takes_the_cases_copied_fields()
    {
        var client = AClient();
        var updated = ClientCopies.ClientUpdatedFromDebtor(client, Diverged(ACopy(client)));
        Assert.Equal(("555-0199", client.Id), (updated.Phone, updated.Id));
    }

    [Fact]
    public void Update_client_keeps_the_clients_own_fields()
    {
        var client = AClient();
        var updated = ClientCopies.ClientUpdatedFromDebtor(client, Diverged(ACopy(client)));
        Assert.Equal((client.DateOfBirth, client.LeadSource), (updated.DateOfBirth, updated.LeadSource));
        Assert.Equal((client.Status, client.CreatedBy), (updated.Status, updated.CreatedBy));
    }

    [Fact]
    public void Update_client_refuses_to_leave_a_nameless_client()
    {
        var client = AClient();
        var copy = ACopy(client);
        var nameless = copy with { Body = copy.Body with { Name = new PersonName() } };
        Assert.Contains("name", Assert.Throws<FieldValidationException>(() => ClientCopies.ClientUpdatedFromDebtor(client, nameless)).Fields.Keys);
    }

    [Fact]
    public void Replace_debtor_is_what_recopy_builds_on()
    {
        var stored = ACopy();
        Assert.Equal(stored.ClientId, DebtorRules.Replace(stored, AsSent(stored)).ClientId);
    }

    // ── The link: one client, one role per case, a linkable client row ──

    private static async Task<(MemoryDebtorStore Store, MemoryFirmStore Firms)> LinkingStore(params FirmClient[] clients)
    {
        var firms = new MemoryFirmStore();
        foreach (var client in clients)
        {
            await firms.CreateClientAsync(client, Ct);
        }

        return (new MemoryDebtorStore(firms), firms);
    }

    [Fact]
    public async Task Linking_into_an_empty_role_is_written()
    {
        var client = AClient();
        var (store, _) = await LinkingStore(client);
        Assert.Equal(LinkOutcome.Written, await store.LinkAsync(ACopy(client), true, Ids.Firm, Ct));
    }

    [Fact]
    public async Task A_create_that_lost_its_race_writes_nothing()
    {
        var (client, other) = (AClient(), AClient());
        var (store, _) = await LinkingStore(client, other);
        var first = ACopy(client);
        await store.LinkAsync(first, true, Ids.Firm, Ct);
        Assert.Equal(LinkOutcome.RoleTaken, await store.LinkAsync(ACopy(other) with { CaseId = first.CaseId }, true, Ids.Firm, Ct));
        Assert.Equal(first, await store.GetAsync(first.CaseId, FilingRole.Debtor1, Ct));
    }

    [Fact]
    public async Task A_client_on_another_role_of_the_case_is_refused()
    {
        var client = AClient();
        var (store, _) = await LinkingStore(client);
        var debtor1 = ACopy(client);
        await store.LinkAsync(debtor1, true, Ids.Firm, Ct);
        var debtor2 = ACopy(client, FilingRole.Debtor2) with { CaseId = debtor1.CaseId };
        Assert.Equal(LinkOutcome.ClientTaken, await store.LinkAsync(debtor2, true, Ids.Firm, Ct));
        Assert.Null(await store.GetAsync(debtor1.CaseId, FilingRole.Debtor2, Ct));
    }

    [Fact]
    public async Task Re_linking_a_role_to_the_client_it_already_holds_is_written()
    {
        var client = AClient();
        var (store, _) = await LinkingStore(client);
        var debtor1 = ACopy(client);
        await store.LinkAsync(debtor1, true, Ids.Firm, Ct);
        Assert.Equal(LinkOutcome.Written, await store.LinkAsync(debtor1, false, Ids.Firm, Ct));
    }

    [Fact]
    public async Task A_merge_claimed_between_the_read_and_the_link_refuses_the_link()
    {
        var (merged, survivor) = (AClient(), AClient());
        var (store, firms) = await LinkingStore(merged, survivor);
        var read = (await firms.GetClientAsync(Ids.Firm, merged.Id, Ct))!;
        Assert.Null(read.RefusalForNewCase());
        var debtor = ACopy(read);
        Assert.True(await firms.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));

        Assert.Equal(LinkOutcome.ClientUnavailable, await store.LinkAsync(debtor, true, Ids.Firm, Ct));
        Assert.Empty(await store.RolesForClientAsync(merged.Id, Ct));
    }

    [Fact]
    public async Task A_client_receiving_a_merge_can_still_be_linked()
    {
        var (merged, survivor) = (AClient(), AClient());
        var (store, firms) = await LinkingStore(merged, survivor);
        await firms.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct);
        Assert.Equal(LinkOutcome.Written, await store.LinkAsync(ACopy(survivor), true, Ids.Firm, Ct));
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("archived-and-merged")]
    [InlineData("merging")]
    public async Task An_unlinkable_client_row_refuses_the_link(string state)
    {
        var fresh = AClient();
        var client = state switch
        {
            "archived" => fresh with { Status = FirmClientStatus.Archived },
            "archived-and-merged" => fresh with { Status = FirmClientStatus.Archived, MergedInto = "client-elsewhere" },
            _ => fresh with { MergingInto = "client-elsewhere" },
        };
        var (store, _) = await LinkingStore(client);
        Assert.Equal(LinkOutcome.ClientUnavailable, await store.LinkAsync(ACopy(client), true, Ids.Firm, Ct));
    }

    [Fact]
    public async Task A_client_of_another_firm_refuses_the_link()
    {
        var client = AClient();
        var (store, _) = await LinkingStore(client);
        Assert.Equal(LinkOutcome.ClientUnavailable, await store.LinkAsync(ACopy(client), true, "another-firm", Ct));
    }

    [Fact]
    public async Task A_link_with_no_firm_store_composed_refuses_to_run() =>
        Assert.Contains("firm store", (await Assert.ThrowsAsync<InvalidOperationException>(() => new MemoryDebtorStore().LinkAsync(ACopy(), true, Ids.Firm, Ct))).Message, StringComparison.Ordinal);

    [Fact]
    public async Task A_case_opened_after_a_merge_claimed_its_client_is_not_written()
    {
        var (merged, survivor) = (AClient(), AClient());
        var (debtors, firms) = await LinkingStore(merged, survivor);
        var cases = new MemoryCaseStore(debtors);
        var (opened, _) = ACase();
        var debtor = ClientCopies.DebtorFromClient(merged, opened, FilingRole.Debtor1);
        await firms.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct);

        var refused = await Assert.ThrowsAsync<ClientUnavailableException>(() => cases.CreateAsync(opened, CaseRules.Assign(opened, Ids.Alice, Ids.Alice), [debtor], Ct));

        Assert.Equal(merged.Id, refused.ClientId);
        Assert.False(cases.Cases.ContainsKey(opened.Id));
        Assert.Empty(await debtors.RolesForClientAsync(merged.Id, Ct));
    }

    // The eleven FakeDynamoDb tests of test_client_acts.py (the link's
    // transaction and its condition checks, a cancellation's reason, the case
    // open's client condition) are the DynamoDB adapter's; dropped — the
    // memory stores above are 18.10's oracle for every one of those outcomes.
}

/// <summary>A client's read of their case, translated from test_case_public_status.py.</summary>
public sealed class CasePublicStatusTests
{
    private const string OtherFirm = "00000000-0000-4000-8000-00000000f18b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Case ACase(int chapter = 13, CaseStatus status = CaseStatus.Intake) =>
        CaseFixtures.NewCase().Case with { Chapter = chapter, Status = status };

    private static ClientAccessor AClient(Case bound, string firmId = Ids.Firm) => new(
        new Firm(firmId, "Example & Partners", FirmStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        new ClientBinding(firmId, bound.Id, "00000000-0000-4000-8000-0000000000aa", "pat@example.test", "Pat Example", [ClientRole.Debtor1], BindingStatus.Active, Ids.Alice, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

    [Fact]
    public void The_projection_is_chapter_and_stage_and_nothing_else() =>
        Assert.Equal(["Chapter", "Stage"], typeof(CasePublicStatus).GetProperties().Where(p => p.Name != "EqualityContract").Select(p => p.Name).Order(StringComparer.Ordinal));

    [Fact]
    public void A_case_projects_to_its_chapter_and_lifecycle_status() =>
        Assert.Equal(new CasePublicStatus(7, CaseStatus.ReadyToFile), CasePublicStatuses.From(ACase(7, CaseStatus.ReadyToFile), Ids.Firm));

    [Fact]
    public void Another_firms_row_projects_to_nothing() =>
        Assert.Null(CasePublicStatuses.From(ACase(), OtherFirm));

    // test_a_row_this_domain_did_not_write_raises: a malformed stored ITEM
    // (no chapter, a boolean chapter, an unknown status) cannot be a Case;
    // dropped — 18.10's mapping refuses such a row on read.

    [Fact]
    public async Task The_memory_store_reads_the_bound_case_without_an_assignment()
    {
        var bound = ACase(status: CaseStatus.Filed);
        var store = new MemoryCaseStore();
        await store.CreateAsync(bound, new CaseAssignment(bound.Id, "staff", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "staff"), cancellationToken: Ct);
        Assert.Equal(new CasePublicStatus(13, CaseStatus.Filed), await store.PublicStatusAsync(AClient(bound), Ct));
    }

    [Fact]
    public async Task The_memory_store_refuses_a_binding_whose_firm_does_not_own_the_case()
    {
        var bound = ACase();
        var store = new MemoryCaseStore();
        await store.CreateAsync(bound, CaseRules.Assign(bound, "staff", "staff"), cancellationToken: Ct);
        Assert.Null(await store.PublicStatusAsync(AClient(bound, OtherFirm), Ct));
    }

    [Fact]
    public async Task The_memory_store_answers_nothing_for_a_missing_case() =>
        Assert.Null(await new MemoryCaseStore().PublicStatusAsync(AClient(ACase()), Ct));

    // The three FakeDynamoDb tests (the projection expression, another firm's
    // row, a missing case) are the DynamoDB adapter's; dropped.
}
