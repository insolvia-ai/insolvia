using System.Text.Json.Nodes;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Core.Provenance;
using Insolvia.Core.TaxIds;

namespace Insolvia.Core.Tests;

/// <summary>The debtor record, translated from test_debtors.py.</summary>
public sealed class DebtorTests
{
    private static DebtorDraft Draft(string json = "{}") => DebtorRules.Parse(Body.Of(json));

    // ── Progressive intake ──────────────────────────────────────────

    [Fact]
    public void An_entirely_empty_body_is_valid()
    {
        var result = Draft();
        Assert.Null(result.Body.Name.Given);
        Assert.Empty(result.Body.OtherNamesUsed);
    }

    [Fact]
    public void One_field_with_its_provenance_is_valid() =>
        Assert.Equal("Ada", Draft("""{"name": {"given": "Ada"}, "provenance": {"name.given": {"source": "staff_typed"}}}""").Body.Name.Given);

    [Fact]
    public void Whitespace_only_text_collapses_to_absent() =>
        Assert.Null(Draft("""{"name": {"given": "   "}}""").Body.Name.Given);

    [Fact]
    public void A_cleared_field_therefore_needs_no_provenance() =>
        Draft("""{"name": {"given": ""}, "phone": "  "}""");

    // ── Shape validation ────────────────────────────────────────────

    [Fact]
    public void Rejects_a_non_string_where_text_belongs() =>
        Assert.Contains("name.given", Assert.Throws<FieldValidationException>(() => Draft("""{"name": {"given": 7}}""")).Fields.Keys);

    [Fact]
    public void Rejects_a_multi_line_value() =>
        Assert.Contains("name.surname", Assert.Throws<FieldValidationException>(() => Draft("""{"name": {"surname": "Love\nlace"}}""")).Fields.Keys);

    [Fact]
    public void Rejects_an_over_long_value() =>
        Assert.Contains("email", Assert.Throws<FieldValidationException>(() => DebtorRules.Parse(new JsonObject { ["email"] = new string('a', 201) })).Fields.Keys);

    [Fact]
    public void Rejects_an_unknown_venue_basis() =>
        Assert.Contains("venue.basis", Assert.Throws<FieldValidationException>(() => Draft("""{"venue": {"basis": "felt_like_it"}}""")).Fields.Keys);

    [Theory]
    [InlineData("completed_with_certificate", CounselingStatus.CompletedWithCertificate)]
    [InlineData("completed_certificate_pending", CounselingStatus.CompletedCertificatePending)]
    [InlineData("exigent_circumstances_waiver_requested", CounselingStatus.ExigentCircumstancesWaiverRequested)]
    [InlineData("not_required", CounselingStatus.NotRequired)]
    public void Accepts_the_four_credit_counseling_statuses(string status, CounselingStatus expected) =>
        Assert.Equal(expected, Draft($$$$"""{"credit_counseling": {"status": "{{{{status}}}}"}, "provenance": {"credit_counseling.status": {"source": "staff_typed"}}}""").Body.CreditCounseling.Status);

    [Fact]
    public void Reports_every_bad_field_at_once()
    {
        var error = Assert.Throws<FieldValidationException>(() => Draft("""{"name": {"given": 1, "surname": 2}, "email": 3}"""));
        Assert.Superset(new HashSet<string> { "name.given", "name.surname", "email" }, error.Fields.Keys.ToHashSet());
    }

    // ── Other names ─────────────────────────────────────────────────

    [Fact]
    public void Keeps_the_caller_supplied_id() =>
        Assert.Equal("n1", Draft("""{"other_names_used": [{"id": "n1", "surname": "Byron"}], "provenance": {"other_names_used[n1].surname": {"source": "staff_typed"}}}""").Body.OtherNamesUsed[0].Id);

    [Fact]
    public void An_alias_without_an_id_is_refused() =>
        Assert.Contains("other_names_used[0].id", Assert.Throws<FieldValidationException>(() => Draft("""{"other_names_used": [{"surname": "Smith"}]}""")).Fields.Keys);

    [Fact]
    public void The_refusal_is_stable_across_attempts()
    {
        IReadOnlyDictionary<string, string> Fields() =>
            Assert.Throws<FieldValidationException>(() => Draft("""{"other_names_used": [{"surname": "Smith"}]}""")).Fields;
        Assert.Equal(Fields(), Fields());
    }

    [Fact]
    public void Rejects_duplicate_ids() =>
        Assert.Contains("other_names_used[1].id", Assert.Throws<FieldValidationException>(() => Draft("""{"other_names_used": [{"id": "n1"}, {"id": "n1"}]}""")).Fields.Keys);

    [Fact]
    public void Rejects_a_string_where_a_list_belongs() =>
        Assert.Contains("other_names_used", Assert.Throws<FieldValidationException>(() => Draft("""{"other_names_used": "Byron"}""")).Fields.Keys);

    // ── The tax id (shape here; the sealing is TaxIdTests') ─────────

    [Fact]
    public void A_tax_id_is_accepted_with_provenance_at_the_one_path() =>
        Assert.Equal(new TaxIdInput(TaxIdKind.Ssn, Value: "987654321"), Draft("""{"tax_id": {"kind": "ssn", "value": "987-65-4321"}, "provenance": {"tax_id": {"source": "staff_typed"}}}""").TaxId);

    [Fact]
    public void A_tax_id_needs_provenance_like_any_populated_field() =>
        Assert.Contains("provenance.tax_id", Assert.Throws<FieldValidationException>(() => Draft("""{"tax_id": {"kind": "ssn", "value": "987-65-4321"}}""")).Fields.Keys);

    [Fact]
    public void A_malformed_number_is_refused_with_its_path() =>
        Assert.Contains("tax_id.value", Assert.Throws<FieldValidationException>(() => Draft("""{"tax_id": {"kind": "ssn", "value": "000-00-0000"}}""")).Fields.Keys);

    [Fact]
    public void The_digits_never_enter_the_body()
    {
        var result = Draft("""{"tax_id": {"kind": "ssn", "value": "987-65-4321"}, "provenance": {"tax_id": {"source": "staff_typed"}}}""");
        Assert.False(DebtorRules.BodyNode(result.Body).ContainsKey("tax_id"));
        Assert.DoesNotContain("987654321", DebtorRules.BodyNode(result.Body).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_carries_a_reference_never_the_number()
    {
        var reference = new TaxIdRef(TaxIdKind.Ssn, "4321", "ref-0001");
        var debtor = DebtorRules.Create(Draft(), "c1", FilingRole.Debtor1, taxId: reference);
        Assert.Equal(reference, debtor.TaxId);
        Assert.False(DebtorRules.BodyNode(debtor.Body).ContainsKey("tax_id"));
    }

    // test_the_record_carries_a_reference_and_serves_the_last_four (the JSON
    // view and item half), test_the_reference_survives_the_item_round_trip and
    // test_a_record_without_one_serialises_without_the_member: the item shape
    // and the wire view; dropped. The reference rides beside the body above.

    [Fact]
    public void Replacing_takes_the_resolved_reference()
    {
        var reference = new TaxIdRef(TaxIdKind.Ssn, "4321", "ref-0001");
        var first = DebtorRules.Create(Draft(), "c1", FilingRole.Debtor1, taxId: reference);
        Assert.Null(DebtorRules.Replace(first, Draft()).TaxId);
        Assert.Equal(reference, DebtorRules.Replace(first, Draft(), reference).TaxId);
    }

    [Fact]
    public void An_explicit_null_tax_id_is_not_an_error() => Assert.Null(Draft("""{"tax_id": null}""").TaxId);

    // ── Provenance ──────────────────────────────────────────────────

    [Fact]
    public void A_value_without_provenance_is_rejected() =>
        Assert.Contains("provenance.name.given", Assert.Throws<FieldValidationException>(() => Draft("""{"name": {"given": "Ada"}}""")).Fields.Keys);

    [Fact]
    public void An_unconfirmed_extraction_cannot_be_saved() =>
        Assert.Throws<FieldValidationException>(() => Draft("""{"name": {"given": "Ada"}, "provenance": {"name.given": {"source": "ai_extracted"}}}"""));

    [Fact]
    public void A_confirmed_extraction_can() =>
        Assert.Equal("staff-1", Draft("""{"name": {"given": "Ada"}, "provenance": {"name.given": {"source": "ai_extracted", "confirmed_by": "staff-1", "confirmed_at": "2026-08-05T12:00:00.000000Z"}}}""").Provenance["name.given"].ConfirmedBy);

    [Fact]
    public void Provenance_is_checked_against_the_stored_shape_not_the_raw_body() =>
        Draft("""{"name": {"given": "   "}, "provenance": {}}""");

    [Fact]
    public void A_bad_field_is_reported_before_bad_provenance()
    {
        var error = Assert.Throws<FieldValidationException>(() => Draft("""{"name": {"given": 7}, "provenance": {"!!": {"source": "staff_typed"}}}"""));
        Assert.Contains("name.given", error.Fields.Keys);
        Assert.DoesNotContain(error.Fields.Keys, key => key.StartsWith("provenance", StringComparison.Ordinal));
    }

    // ── Filing role ─────────────────────────────────────────────────

    [Theory]
    [InlineData("debtor_1", FilingRole.Debtor1)]
    [InlineData("debtor_2", FilingRole.Debtor2)]
    [InlineData("non_filing_spouse", FilingRole.NonFilingSpouse)]
    public void Accepts_the_three_roles(string role, FilingRole expected) =>
        Assert.Equal(expected, DebtorRules.ParseFilingRole(JsonValue.Create(role)));

    [Fact]
    public void Rejects_anything_else() =>
        Assert.Throws<FieldValidationException>(() => DebtorRules.ParseFilingRole(JsonValue.Create("debtor_3")));

    [Fact]
    public void The_filing_roles_keep_their_wire_strings() =>
        Assert.Equal(["debtor_1", "debtor_2", "non_filing_spouse"], Wire.Names<FilingRole>());

    // ── Identity and replacement ────────────────────────────────────

    [Fact]
    public void Create_stamps_server_owned_identity()
    {
        var debtor = DebtorRules.Create(Draft(), "c1", FilingRole.Debtor1);
        Assert.Equal(("c1", FilingRole.Debtor1), (debtor.CaseId, debtor.FilingRole));
        Assert.NotEmpty(debtor.Id);
        Assert.Equal(debtor.CreatedAt, debtor.UpdatedAt);
    }

    [Fact]
    public void Replacing_keeps_the_id_and_created_at()
    {
        var first = DebtorRules.Create(Draft(), "c1", FilingRole.Debtor1);
        var second = DebtorRules.Replace(first, Draft("""{"name": {"given": "Ada"}, "provenance": {"name.given": {"source": "staff_typed"}}}"""));
        Assert.Equal((first.Id, first.CreatedAt), (second.Id, second.CreatedAt));
        Assert.Equal("Ada", second.Body.Name.Given);
    }

    [Fact]
    public void Replacing_drops_a_field_the_new_body_omits()
    {
        var first = DebtorRules.Replace(
            DebtorRules.Create(Draft(), "c1", FilingRole.Debtor1),
            Draft("""{"name": {"given": "Ada"}, "provenance": {"name.given": {"source": "staff_typed"}}}"""));
        Assert.Null(DebtorRules.Replace(first, Draft()).Body.Name.Given);
    }

    [Fact]
    public void A_client_link_and_its_case_creation_travel_together() =>
        Assert.Throws<ArgumentException>(() => DebtorRules.Create(Draft(), "c1", FilingRole.Debtor1, clientId: "client-1"));

    // ── Serialisation ───────────────────────────────────────────────
    // test_json_omits_absent_values, test_json_keeps_the_values_that_are_there,
    // test_the_item_keys_put_a_debtor_in_its_case_partition: the JSON view and
    // the item keys; dropped. test_an_item_round_trips and
    // test_a_stored_item_is_re_parsed_rather_than_trusted become the stored
    // body's round trip, which is what 18.10's column must preserve:

    [Fact]
    public void A_stored_body_round_trips()
    {
        var draft = Draft("""
            {"name": {"given": "Ada", "surname": "Lovelace"}, "other_names_used": [{"id": "n1", "surname": "Byron"}],
             "residence_address": {"city": "London"}, "venue": {"basis": "lived_longest_180_days"},
             "provenance": {"name.given": {"source": "staff_typed"}, "name.surname": {"source": "staff_typed"},
                            "other_names_used[n1].surname": {"source": "staff_typed"}, "residence_address.city": {"source": "staff_typed"},
                            "venue.basis": {"source": "staff_typed"}}}
            """);
        var stored = BodyJson.Prune(DebtorRules.BodyNode(draft.Body));
        stored["provenance"] = ProvenanceRules.ToJson(draft.Provenance);
        var restored = DebtorRules.Parse(stored, enforceProvenance: false);
        Assert.Equal(draft.Body, restored.Body);
        Assert.Equal(draft.Provenance, restored.Provenance);
    }

    [Fact]
    public void A_stored_body_is_re_parsed_rather_than_trusted() =>
        Assert.Throws<FieldValidationException>(() => DebtorRules.Parse(Body.Of("""{"name": {"given": 7}}"""), enforceProvenance: false));

    [Fact]
    public void A_bare_debtor_needs_only_identity()
    {
        var debtor = new Debtor("d1", "c1", FilingRole.Debtor1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DebtorBody.Empty);
        Assert.Empty(debtor.Body.OtherNamesUsed);
        Assert.Null(debtor.Body.Name.Given);
    }

    // ── Alias ids stay addressable ──────────────────────────────────

    [Theory]
    [InlineData("n.1")]
    [InlineData("n]1")]
    [InlineData("n 1")]
    [InlineData("urn:x:1")]
    public void An_unaddressable_id_is_refused_with_a_reason(string bad) =>
        Assert.Contains("other_names_used[0].id", Assert.Throws<FieldValidationException>(() => Draft($$$"""{"other_names_used": [{"id": "{{{bad}}}", "surname": "Byron"}]}""")).Fields.Keys);

    [Fact]
    public void A_uuid_is_fine() =>
        Assert.StartsWith("3f9c", Draft("""{"other_names_used": [{"id": "3f9c1a2b-0000-4000-8000-000000000000"}]}""").Body.OtherNamesUsed[0].Id, StringComparison.Ordinal);

    [Fact]
    public void An_accepted_id_is_always_addressable() =>
        Assert.All(Draft("""{"other_names_used": [{"id": "n1"}, {"id": "3f9c-1a2b"}]}""").Body.OtherNamesUsed, alias => Assert.Matches(ProvenanceRules.AddressableId(), alias.Id));

    // ── Role ordering ───────────────────────────────────────────────

    [Fact]
    public void Roles_order_as_the_forms_print_them() =>
        Assert.Equal(
            [FilingRole.Debtor1, FilingRole.Debtor2, FilingRole.NonFilingSpouse],
            new[] { FilingRole.NonFilingSpouse, FilingRole.Debtor2, FilingRole.Debtor1 }.OrderBy(DebtorRules.RoleOrder));

    // test_an_unknown_role_sorts_last_rather_than_raising: a FilingRole is a
    // closed enum, so an unknown role cannot reach RoleOrder; dropped.

    [Fact]
    public void The_order_does_not_depend_on_the_names_sorting_alphabetically()
    {
        Assert.Equal(0, DebtorRules.RoleOrder(FilingRole.Debtor1));
        Assert.Equal([0, 1, 2], DebtorRules.FilingRoles.Select(DebtorRules.RoleOrder));
    }

    // ── Signature date ──────────────────────────────────────────────

    [Fact]
    public void Accepts_an_iso_calendar_date() =>
        Assert.Equal(new DateOnly(2026, 8, 5), Draft("""{"signed_at": "2026-08-05", "provenance": {"signed_at": {"source": "staff_typed"}}}""").Body.SignedAt);

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2019-02-30")]
    [InlineData("2026-08-05T12:00:00Z")]
    [InlineData("20260805")]
    public void Rejects_anything_but_a_calendar_date(string signedAt) =>
        Assert.Contains("signed_at", Assert.Throws<FieldValidationException>(() => DebtorRules.Parse(new JsonObject { ["signed_at"] = signedAt })).Fields.Keys);

    [Fact]
    public void The_debtor_vocabularies_keep_their_wire_strings()
    {
        Assert.Equal(["lived_longest_180_days", "other"], Wire.Names<VenueBasis>());
        Assert.Equal(["incapacity", "disability", "active_duty"], Wire.Names<CounselingExemption>());
        Assert.Equal(["written", "role_taken", "client_taken", "client_unavailable"], Wire.Names<LinkOutcome>());
    }
}

/// <summary>The memory debtor store, translated from test_debtor_store.py's MemoryDebtorStore half.</summary>
public sealed class DebtorStoreTests
{
    private const string CaseId = "case-0001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // test_debtor_store.py's DynamoDB attribute-codec tests (every value
    // round-trips, booleans vs numbers, floats in provenance, the item keys)
    // test the boto3 converter; dropped — no successor.

    [Fact]
    public async Task Put_then_get_returns_the_debtor()
    {
        var store = new MemoryDebtorStore();
        var debtor = ADebtor();
        await store.PutAsync(debtor, Ct);
        Assert.Equal(debtor, await store.GetAsync(CaseId, FilingRole.Debtor1, Ct));
    }

    [Fact]
    public async Task Get_of_a_role_that_was_never_written_is_none()
    {
        var store = new MemoryDebtorStore();
        await store.PutAsync(ADebtor(), Ct);
        Assert.Null(await store.GetAsync(CaseId, FilingRole.Debtor2, Ct));
    }

    [Fact]
    public async Task Get_is_scoped_to_the_case()
    {
        var store = new MemoryDebtorStore();
        await store.PutAsync(ADebtor(caseId: "case-0001"), Ct);
        Assert.Null(await store.GetAsync("case-0002", FilingRole.Debtor1, Ct));
    }

    [Fact]
    public async Task Put_replaces_the_whole_record()
    {
        var store = new MemoryDebtorStore();
        await store.PutAsync(ADebtor(surname: "Nakamura"), Ct);
        await store.PutAsync(ADebtor(surname: "Okafor"), Ct);
        Assert.Equal("Okafor", (await store.GetAsync(CaseId, FilingRole.Debtor1, Ct))!.Body.Name.Surname);
        Assert.Single(await store.ListForCaseAsync(CaseId, Ct));
    }

    [Fact]
    public async Task List_for_case_returns_only_that_cases_debtors()
    {
        var store = new MemoryDebtorStore();
        await store.PutAsync(ADebtor(caseId: "case-0001", role: FilingRole.Debtor1), Ct);
        await store.PutAsync(ADebtor(caseId: "case-0001", role: FilingRole.Debtor2), Ct);
        await store.PutAsync(ADebtor(caseId: "case-0002", role: FilingRole.Debtor1), Ct);
        Assert.Equal([FilingRole.Debtor1, FilingRole.Debtor2], (await store.ListForCaseAsync("case-0001", Ct)).Select(d => d.FilingRole));
    }

    [Fact]
    public async Task List_for_case_of_a_case_with_no_debtors_is_empty() =>
        Assert.Empty(await new MemoryDebtorStore().ListForCaseAsync(CaseId, Ct));

    [Fact]
    public async Task List_for_case_is_in_filing_role_order()
    {
        var store = new MemoryDebtorStore();
        foreach (var role in DebtorRules.FilingRoles.Reverse())
        {
            await store.PutAsync(ADebtor(role: role), Ct);
        }

        Assert.Equal(DebtorRules.FilingRoles, (await store.ListForCaseAsync(CaseId, Ct)).Select(d => d.FilingRole));
    }

    // test_ordering_is_by_position_on_the_form_not_alphabetical used a role the
    // list does not know; a closed enum cannot carry one. Dropped.

    [Fact]
    public async Task A_first_save_does_not_overwrite_another()
    {
        var store = new MemoryDebtorStore();
        Assert.True(await store.CreateAsync(ADebtor(surname: "First"), Ct));
        Assert.False(await store.CreateAsync(ADebtor(surname: "Second"), Ct));
        Assert.Equal("First", (await store.GetAsync(CaseId, FilingRole.Debtor1, Ct))!.Body.Name.Surname);
    }

    private static Debtor ADebtor(string caseId = CaseId, FilingRole role = FilingRole.Debtor1, string surname = "Nakamura") =>
        new($"debtor-{role.ToWire()}", caseId, role, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DebtorBody.Empty with { Name = new PersonName(Surname: surname) })
        {
            Provenance = new Dictionary<string, ProvenanceEntry> { ["name.surname"] = new(ProvenanceSource.StaffTyped) }.ToValueDictionary(),
        };
}

/// <summary>The memory case-entity store, translated from test_case_entity_store.py.</summary>
public sealed class CaseEntityStoreTests
{
    private const string CaseId = "case-0001";
    private const string OtherCase = "case-0002";

    private readonly MemoryCaseEntityStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Created_entities_are_read_back()
    {
        var entity = Creditor();
        await _store.CreateAsync(entity, Ct);
        Assert.Equal(entity, await _store.GetAsync(CaseId, Creditors.CreditorRules.Kind, entity.Id, Ct));
    }

    [Fact]
    public async Task Creating_the_same_id_twice_raises()
    {
        var entity = Creditor();
        await _store.CreateAsync(entity, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateAsync(entity, Ct));
    }

    [Fact]
    public async Task Get_is_case_scoped()
    {
        var entity = Creditor();
        await _store.CreateAsync(entity, Ct);
        Assert.Null(await _store.GetAsync(OtherCase, Creditors.CreditorRules.Kind, entity.Id, Ct));
    }

    [Fact]
    public async Task Get_is_kind_scoped()
    {
        var entity = Creditor();
        await _store.CreateAsync(entity, Ct);
        Assert.Null(await _store.GetAsync(CaseId, Expenses.ExpenseRules.Expense, entity.Id, Ct));
    }

    [Fact]
    public async Task Put_replaces_an_existing_record()
    {
        var entity = Creditor();
        await _store.CreateAsync(entity, Ct);
        var renamed = CaseEntityRules.Replace(entity, CaseEntityRules.ParseEntity(Creditors.CreditorRules.Kind, Body.Of("""{"name": "Renamed", "provenance": {"name": {"source": "staff_typed"}}}""")));
        Assert.True(await _store.PutAsync(renamed, Ct));
        Assert.Equal("Renamed", (await _store.GetTypedAsync(CaseId, Creditors.CreditorRules.Kind, entity.Id, Ct))!.Body.Name);
    }

    [Fact]
    public async Task Put_refuses_to_resurrect_a_deleted_record()
    {
        var entity = Creditor();
        await _store.CreateAsync(entity, Ct);
        Assert.True(await _store.DeleteAsync(CaseId, Creditors.CreditorRules.Kind, entity.Id, Ct));
        Assert.False(await _store.PutAsync(entity, Ct));
        Assert.Null(await _store.GetAsync(CaseId, Creditors.CreditorRules.Kind, entity.Id, Ct));
    }

    [Fact]
    public async Task Delete_reports_whether_this_call_removed_it()
    {
        var entity = Creditor();
        await _store.CreateAsync(entity, Ct);
        Assert.True(await _store.DeleteAsync(CaseId, Creditors.CreditorRules.Kind, entity.Id, Ct));
        Assert.False(await _store.DeleteAsync(CaseId, Creditors.CreditorRules.Kind, entity.Id, Ct));
    }

    [Fact]
    public async Task Listing_is_scoped_and_in_creation_order()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var first = Creditor(name: "Alpha", clock: clock);
        clock.Now = clock.Now.AddSeconds(1);
        var second = Creditor(name: "Beta", clock: clock);
        var foreign = Creditor(caseId: OtherCase, name: "Gamma", clock: clock);
        await _store.CreateAsync(second, Ct);
        await _store.CreateAsync(first, Ct);
        await _store.CreateAsync(foreign, Ct);
        var listed = await _store.ListForCaseAsync(CaseId, Creditors.CreditorRules.Kind, Ct);
        Assert.Equal([first.Id, second.Id], listed.Select(entity => entity.Id));
        Assert.All(listed, entity => Assert.Equal(CaseId, entity.CaseId));
    }

    [Fact]
    public async Task Listing_an_empty_collection_is_empty() =>
        Assert.Empty(await _store.ListForCaseAsync(CaseId, Creditors.CreditorRules.Kind, Ct));

    private static CaseEntity<Creditors.CreditorBody> Creditor(string caseId = CaseId, string name = "Example Bank", TimeProvider? clock = null)
    {
        var draft = CaseEntityRules.ParseEntity(Creditors.CreditorRules.Kind, new JsonObject
        {
            ["name"] = name,
            ["provenance"] = new JsonObject { ["name"] = new JsonObject { ["source"] = "staff_typed" } },
        });
        return CaseEntityRules.Create(Creditors.CreditorRules.Kind, draft, caseId, clock);
    }
}
