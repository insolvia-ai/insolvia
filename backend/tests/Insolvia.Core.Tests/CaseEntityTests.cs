using System.Text.Json.Nodes;
using Insolvia.Core.Assets;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Claims;
using Insolvia.Core.Codebtors;
using Insolvia.Core.ContractLeases;
using Insolvia.Core.Creditors;
using Insolvia.Core.Exemptions;
using Insolvia.Core.Expenses;
using Insolvia.Core.Provenance;

namespace Insolvia.Core.Tests;

/// <summary>The machinery every case collection shares, translated from test_case_entities.py.</summary>
public sealed class CaseEntityTests
{
    private const string CaseId = "case-0001";

    /// <summary>A populated body for every collection — Python's <c>SAMPLE_BODIES</c>.</summary>
    internal static readonly IReadOnlyDictionary<string, string> SampleBodies = new Dictionary<string, string>
    {
        ["creditors"] = """{"name": "Example Bank", "address": {"line1": "1 Example Way"}}""",
        ["claims"] = """
            {"creditor_id": "cr-1", "claim_class": "secured", "amount": "12500.00", "contingent": false, "asset_id": "as-1",
             "lien_position": 1, "collateral_description": "2016 sedan", "collateral_value": "9000.00", "lien_nature": ["agreement"],
             "unsecured_amount_override": "3500.00", "intention": "retain_reaffirm", "intention_explanation": "Keeping the car; payments are current.",
             "notice_parties": [{"id": "np1", "name": "Example Servicer"}]}
            """,
        ["assets"] = """
            {"category": "vehicle", "description": "2016 sedan, 90k miles", "value_entire": "9000.00", "value_portion_owned": "9000.00",
             "ownership_interest": "debtor_1", "community_property": false, "year": 2016, "make": "Example Motors", "model": "Sedan LX",
             "mileage": 90000, "includes_personal_information": false}
            """,
        ["employments"] = """
            {"debtor_id": "d-1", "status": "employed", "occupation": "Example occupation", "employer_name": "Example Employer",
             "employer_address": {"city": "Exampleville"}, "employed_since": "2019-02-14"}
            """,
        ["income_summaries"] = """{"debtor_id": "d-1", "wages": "5200.00", "deduction_tax": "830.00", "change_expected": false}""",
        ["pay_period_records"] = """
            {"employment_id": "emp-1", "period_start": "2026-08-01", "period_end": "2026-08-14", "pay_date": "2026-08-19",
             "gross": "2400.00", "net": "1890.50", "frequency": "biweekly",
             "deductions": [{"id": "ded1", "category": "tax", "amount": "410.00"},
                            {"id": "ded2", "category": "insurance", "amount": "99.50", "description": "Medical premium"}]}
            """,
        ["households"] = """{"which_household": "main", "separate_household": false}""",
        ["expenses"] = """{"household_id": "h-1", "category": "rent_or_home_ownership", "amount": "1800.00"}""",
        ["dependents"] = """{"household_id": "h-1", "relationship": "daughter", "age": 9, "lives_with_debtor": true}""",
        ["codebtors"] = """{"name": "Example Cosigner", "address": {"city": "Exampleville"}, "claim_ids": ["cl-1"]}""",
        ["sofa_entries"] = """
            {"entry_type": "gift", "payload": {"recipient": {"name": "Example Recipient"}, "relationship": "niece",
             "dates": ["2025-12-25"], "value": "700.00"}}
            """,
        ["petitions"] = """
            {"fee_handling": "installments", "rents_residence": true, "eviction_judgment_against_you": false,
             "small_business_status": "not_filing_under_chapter_11",
             "hazardous_property": {"description": "Propane tanks at a former stand", "why_immediate": "Leak risk", "address": {"city": "Exampleville"}},
             "debt_character": "consumer", "ch7_funds_available_for_creditors": false, "estimated_creditors": "1_49",
             "estimated_assets": "0_50000", "estimated_liabilities": "50001_100000"}
            """,
        ["prior_cases"] = """{"district": "Middle District of Florida", "filed_on": "2019-03-04", "case_number": "19-01234"}""",
        ["related_cases"] = """
            {"debtor_name": "Example Spouse", "relationship": "Spouse", "district": "Middle District of Florida",
             "filed_on": "2026-01-15", "case_number": "26-00042"}
            """,
        ["sole_proprietorships"] = """{"name": "Example Lawn Care", "address": {"city": "Exampleville"}, "business_type": "none_of_the_above"}""",
        ["filing_professionals"] = """
            {"role": "attorney", "name": {"given": "Alex", "surname": "Counsel"}, "firm_name": "Counsel & Counsel PA",
             "address": {"line1": "1 Example Way", "city": "Exampleville"}, "phone": "(305) 555-0100", "email": "alex@example.com",
             "bar_number": "112233", "bar_state": "FL", "signature_date": "2026-09-01"}
            """,
        ["exemptions"] = """
            {"asset_id": "as-1", "statute_citation": "Fla. Stat. § 222.25(1)", "amount": "5000.00", "claims_full_fmv": false,
             "acquired_within_1215_days": false}
            """,
        ["contract_leases"] = """
            {"counterparty_name": "Example Storage LLC", "counterparty_address": {"line1": "1 Example Way", "city": "Exampleville"},
             "description": "Month-to-month storage unit lease, unit 12", "intention": "assume", "list_on_statement_of_intention": true}
            """,
        ["community_household_members"] = """
            {"name": "Example Former Spouse", "address": {"city": "Exampleville", "state": "TX"}, "community_state": "TX", "lived_with_debtor": false}
            """,
        ["other_income_records"] = """
            {"debtor_id": "d-1", "category": "unemployment", "received_on": "2026-04-07", "amount": "275.00", "payer": "Example State Agency"}
            """,
        ["means_test_inputs"] = """
            {"people_under_65": 3, "people_65_or_older": 0,
             "marital_adjustments": [{"id": "ma1", "description": "Spouse's own student loan", "amount": "220.00"}],
             "home_secured_monthly_total": "1450.00", "vehicle_count": 1, "vehicle_1_loan_monthly": "310.00", "taxes": "780.00",
             "involuntary_deductions": "95.00", "health_insurance": "410.00",
             "other_secured_payments": [{"id": "os1", "creditor_name": "Example Finance", "property_description": "Boat", "monthly_payment": "120.00"}],
             "priority_cure_total": "2400.00", "ch13_eligible": true, "ch13_projected_plan_payment": "500.00",
             "marital_filing_status": "married_not_filing_same_household", "median_household_size": 3, "irs_family_size": 2,
             "irs_housing_family_size": 3, "non_consumer_debts": false, "disabled_veteran": false, "reservist_national_guard": false,
             "income_overrides": [{"id": "ov1", "column": "A", "category": "wages", "monthly_amount": "4100.00"}]}
            """,
        ["plans"] = """
            {"term_months": 60, "payment_source": "fixed", "monthly_payment": "850.00",
             "step_payments": [{"id": "st1", "start_month": 25, "monthly_payment": "1000.00"}],
             "lump_sums": [{"id": "ls1", "month": 13, "amount": "2000.00", "description": "Refund"}],
             "trustee_percentage": "8.5", "attorney_fees": "3500.00",
             "secured_treatments": [{"id": "tr1", "claim_id": "c-1", "treatment": "cramdown", "cramdown_value": "9000.00", "interest_rate": "7.25"}],
             "priority_percentage": "100", "unsecured_treatment": "pot", "chapter_7_other_costs": "1500.00"}
            """,
    };

    public static TheoryData<string> Collections() => [.. CaseCollections.ByCollection.Keys.Order(StringComparer.Ordinal)];

    /// <summary>A staff_typed entry for every populated path — built with the server's own walk.</summary>
    internal static JsonObject TypedProvenance(string collection, JsonObject body)
    {
        var draft = CaseCollections.ByCollection[collection].ParseDraft(body, enforceProvenance: false);
        var provenance = new JsonObject();
        foreach (var path in ProvenanceRules.PopulatedPaths(draft.BodyNode()))
        {
            provenance[path] = new JsonObject { ["source"] = "staff_typed" };
        }

        return provenance;
    }

    internal static JsonObject SamplePayload(string collection)
    {
        var body = Body.Of(SampleBodies[collection]);
        body["provenance"] = TypedProvenance(collection, body);
        return body;
    }

    [Fact]
    public void Every_collection_has_a_sample_body() =>
        Assert.Equal(CaseCollections.ByCollection.Keys.Order(StringComparer.Ordinal), SampleBodies.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void Collection_keys_match_their_kinds() =>
        Assert.All(CaseCollections.ByCollection, pair => Assert.Equal(pair.Key, pair.Value.Collection));

    [Fact]
    public void Sort_key_namespaces_cannot_collide()
    {
        var all = CaseCollections.Kinds.Select(kind => kind.SkPrefix).Concat(CaseCollections.ReservedNamespaces).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public void A_populated_body_with_full_provenance_parses(string collection) =>
        Assert.NotEmpty(CaseCollections.ByCollection[collection].ParseDraft(SamplePayload(collection)).Provenance);

    [Theory]
    [MemberData(nameof(Collections))]
    public void A_populated_field_without_provenance_is_rejected(string collection)
    {
        var error = Assert.Throws<FieldValidationException>(() => CaseCollections.ByCollection[collection].ParseDraft(Body.Of(SampleBodies[collection])));
        Assert.All(error.Fields.Keys, path => Assert.StartsWith("provenance.", path, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public void An_empty_body_parses_with_no_provenance(string collection) =>
        Assert.Empty(CaseCollections.ByCollection[collection].ParseDraft([]).Provenance);

    [Theory]
    [MemberData(nameof(Collections))]
    public void An_unconfirmed_machine_value_cannot_be_stored(string collection)
    {
        var body = Body.Of(SampleBodies[collection]);
        var provenance = TypedProvenance(collection, body);
        var first = provenance.First().Key;
        provenance[first] = new JsonObject { ["source"] = "ai_extracted" };
        body["provenance"] = provenance;
        var error = Assert.Throws<FieldValidationException>(() => CaseCollections.ByCollection[collection].ParseDraft(body));
        Assert.Contains($"provenance.{first}", error.Fields.Keys);
    }

    [Fact]
    public void A_confirmed_machine_value_is_accepted()
    {
        var draft = CaseEntityRules.ParseEntity(CreditorRules.Kind, Body.Of("""
            {"name": "Example Bank", "provenance": {"name": {"source": "ai_extracted", "confirmed_by": "00000000-0000-4000-8000-00000000a11c",
             "confirmed_at": "2026-08-01T12:00:00.000000Z", "confidence": 0.93}}}
            """));
        Assert.NotNull(draft.Provenance["name"].ConfirmedBy);
    }

    // test_an_entity_survives_the_item_round_trip becomes the STORED BODY's
    // round trip — what 18.10's jsonb column must preserve: the body as JSON,
    // re-parsed by its kind, is the same body. It also proves every body
    // member serializes under the name its parser reads.
    [Theory]
    [MemberData(nameof(Collections))]
    public void An_entity_survives_the_stored_body_round_trip(string collection)
    {
        var kind = CaseCollections.ByCollection[collection];
        var draft = kind.ParseDraft(SamplePayload(collection));
        var stored = BodyJson.Prune(draft.BodyNode());
        stored["provenance"] = ProvenanceRules.ToJson(draft.Provenance);
        var restored = kind.ParseDraft(stored, enforceProvenance: false);
        Assert.Equal(draft.Body, restored.Body);
        Assert.Equal(draft.Provenance, restored.Provenance);
    }

    // test_the_item_lives_in_the_case_partition_under_its_prefix: the DynamoDB
    // key shape; dropped (the Postgres key is (case_id, kind, id), 18.10's).

    [Fact]
    public void The_stored_body_omits_absent_members_and_keeps_false()
    {
        var entity = CaseEntityRules.Create(ClaimRules.Kind, CaseEntityRules.ParseEntity(ClaimRules.Kind, SamplePayload("claims")), CaseId);
        var body = BodyJson.Prune(entity.BodyNode());
        Assert.False(Json.AsBool(body["contingent"]));
        Assert.False(body.ContainsKey("disputed"));
        Assert.Equal(CaseId, entity.CaseId);
        Assert.NotEmpty(entity.Provenance);
    }

    [Fact]
    public void Amended_defaults_false()
    {
        var draft = CaseEntityRules.ParseEntity(CreditorRules.Kind, []);
        Assert.False(draft.Amended);
        Assert.False(CaseEntityRules.Create(CreditorRules.Kind, draft, CaseId).Amended);
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public void Amended_true_parses_for_every_collection(string collection) =>
        Assert.True(CaseCollections.ByCollection[collection].ParseDraft(Body.Of("""{"amended": true}""")).Amended);

    [Fact]
    public void Amended_must_be_a_boolean()
    {
        var error = Assert.Throws<FieldValidationException>(() => CaseEntityRules.ParseEntity(CreditorRules.Kind, Body.Of("""{"amended": "yes"}""")));
        Assert.Contains("amended", error.Fields.Keys);
    }

    [Fact]
    public void Amended_is_not_body_data_and_needs_no_provenance() =>
        Assert.Empty(CaseEntityRules.ParseEntity(CreditorRules.Kind, Body.Of("""{"amended": true}""")).Provenance);

    // test_amended_survives_the_item_round_trip,
    // test_amended_is_always_present_on_the_wire and
    // test_an_item_written_before_issue_370_reads_as_not_amended: item shape and
    // JSON view; dropped. An absent `amended` reading as false is
    // Amended_defaults_false above.

    [Fact]
    public void Replace_keeps_amended_from_the_new_draft()
    {
        var entity = CaseEntityRules.Create(CreditorRules.Kind, CaseEntityRules.ParseEntity(CreditorRules.Kind, []), CaseId);
        Assert.False(entity.Amended);
        Assert.True(CaseEntityRules.Replace(entity, CaseEntityRules.ParseEntity(CreditorRules.Kind, Body.Of("""{"amended": true}"""))).Amended);
    }

    [Fact]
    public void Replace_keeps_id_and_created_at()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-08-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var entity = CaseEntityRules.Create(CreditorRules.Kind, CaseEntityRules.ParseEntity(CreditorRules.Kind, SamplePayload("creditors")), CaseId, clock);
        clock.Now = clock.Now.AddMinutes(5);
        var body = Body.Of("""{"name": "Renamed Bank"}""");
        body["provenance"] = TypedProvenance("creditors", body);
        var replaced = CaseEntityRules.Replace(entity, CaseEntityRules.ParseEntity(CreditorRules.Kind, body), clock);
        Assert.Equal((entity.Id, entity.CaseId, entity.CreatedAt), (replaced.Id, replaced.CaseId, replaced.CreatedAt));
        Assert.Equal("Renamed Bank", replaced.Body.Name);
        Assert.True(replaced.UpdatedAt > entity.UpdatedAt);
    }

    // ── The per-entity shape rules ─────────────────────────────────

    [Theory]
    [InlineData("1,200.00")]
    [InlineData("12.345")]
    [InlineData("-5.00")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("abc")]
    public void A_malformed_amount_is_rejected(string amount)
    {
        var error = Assert.Throws<FieldValidationException>(() => ClaimRules.Parse(new JsonObject { ["amount"] = amount }));
        Assert.Contains("amount", error.Fields.Keys);
    }

    [Fact]
    public void An_amount_sent_as_a_number_is_rejected() =>
        Assert.Throws<FieldValidationException>(() => ClaimRules.Parse(Body.Of("""{"amount": 1200.0}""")));

    [Fact]
    public void An_amount_is_canonicalised_to_two_places()
    {
        Assert.Equal("1200.00", ClaimRules.Parse(Body.Of("""{"amount": "1200"}""")).Amount!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("1200.50", ClaimRules.Parse(Body.Of("""{"amount": "1200.5"}""")).Amount!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void An_unknown_enum_member_is_rejected()
    {
        var error = Assert.Throws<FieldValidationException>(() => ClaimRules.Parse(Body.Of("""{"claim_class": "unsecured"}""")));
        Assert.Contains("claim_class", error.Fields.Keys);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("100")]
    [InlineData("\"1\"")]
    [InlineData("1.0")]
    [InlineData("true")]
    public void A_lien_position_outside_one_to_ninety_nine_is_refused(string position)
    {
        var error = Assert.Throws<FieldValidationException>(() => ClaimRules.Parse(new JsonObject { ["lien_position"] = Body.Value(position) }));
        Assert.Contains("lien_position", error.Fields.Keys);
    }

    [Fact]
    public void A_lien_position_of_one_is_the_senior_lien()
    {
        Assert.Equal(1, ClaimRules.Parse(Body.Of("""{"lien_position": 1}""")).LienPosition);
        Assert.Equal(3, ClaimRules.Parse(Body.Of("""{"lien_position": 3}""")).LienPosition);
    }

    [Fact]
    public void An_unknown_intention_is_refused()
    {
        var error = Assert.Throws<FieldValidationException>(() => ClaimRules.Parse(Body.Of("""{"intention": "keep"}""")));
        Assert.Contains("intention", error.Fields.Keys);
    }

    [Fact]
    public void The_unsecured_override_is_money_like_every_other_amount()
    {
        Assert.Equal(1200.00m, ClaimRules.Parse(Body.Of("""{"unsecured_amount_override": "1200"}""")).UnsecuredAmountOverride);
        var error = Assert.Throws<FieldValidationException>(() => ClaimRules.Parse(Body.Of("""{"unsecured_amount_override": "-1"}""")));
        Assert.Contains("unsecured_amount_override", error.Fields.Keys);
    }

    [Fact]
    public void A_notice_party_without_an_id_is_refused()
    {
        var error = Assert.Throws<FieldValidationException>(() => ClaimRules.Parse(Body.Of("""{"notice_parties": [{"name": "Example Servicer"}]}""")));
        Assert.Contains("notice_parties[0].id", error.Fields.Keys);
    }

    [Fact]
    public void A_dependents_name_is_refused_not_dropped()
    {
        var error = Assert.Throws<FieldValidationException>(() => ExpenseRules.ParseDependent(Body.Of("""{"name": "A Child"}""")));
        Assert.Contains("name", error.Fields.Keys);
    }

    [Fact]
    public void An_age_of_true_is_not_a_count()
    {
        var error = Assert.Throws<FieldValidationException>(() => ExpenseRules.ParseDependent(Body.Of("""{"age": true}""")));
        Assert.Contains("age", error.Fields.Keys);
    }

    [Fact]
    public void A_cleared_field_collapses_to_absent() =>
        Assert.Null(CreditorRules.Parse(Body.Of("""{"name": "   "}""")).Name);

    [Fact]
    public void An_exemption_accepts_amount_and_election_together()
    {
        var body = ExemptionRules.Parse(Body.Of("""{"amount": "5000.00", "claims_full_fmv": true}"""));
        Assert.Equal((5000.00m, true), (body.Amount!.Value, body.ClaimsFullFmv!.Value));
    }

    [Fact]
    public void A_community_state_is_a_two_letter_code()
    {
        var error = Assert.Throws<FieldValidationException>(() => CodebtorRules.ParseCommunityHouseholdMember(Body.Of("""{"community_state": "Texas"}""")));
        Assert.Contains("community_state", error.Fields.Keys);
    }

    [Fact]
    public void A_contract_lease_intention_is_assume_or_reject()
    {
        var error = Assert.Throws<FieldValidationException>(() => ContractLeaseRules.Parse(Body.Of("""{"intention": "renegotiate"}""")));
        Assert.Contains("intention", error.Fields.Keys);
    }

    [Fact]
    public void A_contract_lease_statement_of_intention_flag_is_a_boolean()
    {
        var error = Assert.Throws<FieldValidationException>(() => ContractLeaseRules.Parse(Body.Of("""{"list_on_statement_of_intention": "yes"}""")));
        Assert.Contains("list_on_statement_of_intention", error.Fields.Keys);
    }

    [Fact]
    public void A_vehicle_year_and_mileage_parse_as_whole_numbers()
    {
        var body = AssetRules.Parse(Body.Of("""{"category": "vehicle", "year": 2016, "mileage": 92000}"""));
        Assert.Equal((2016, 92000), (body.Year!.Value, body.Mileage!.Value));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("2101")]
    [InlineData("\"2016\"")]
    [InlineData("2016.0")]
    [InlineData("true")]
    public void A_vehicle_year_outside_the_sane_range_is_refused(string year)
    {
        var error = Assert.Throws<FieldValidationException>(() => AssetRules.Parse(new JsonObject { ["year"] = Body.Value(year) }));
        Assert.Contains("year", error.Fields.Keys);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1000000")]
    [InlineData("\"92000\"")]
    [InlineData("92000.0")]
    [InlineData("true")]
    public void A_vehicle_mileage_outside_the_sane_range_is_refused(string mileage)
    {
        var error = Assert.Throws<FieldValidationException>(() => AssetRules.Parse(new JsonObject { ["mileage"] = Body.Value(mileage) }));
        Assert.Contains("mileage", error.Fields.Keys);
    }

    [Fact]
    public void Vehicle_make_and_model_are_single_line_text()
    {
        var body = AssetRules.Parse(Body.Of("""{"make": "Honda", "model": "Civic LX"}"""));
        Assert.Equal(("Honda", "Civic LX"), (body.Make, body.Model));
        var error = Assert.Throws<FieldValidationException>(() => AssetRules.Parse(Body.Of("""{"make": "Honda\nCivic"}""")));
        Assert.Contains("make", error.Fields.Keys);
    }

    [Fact]
    public void The_pii_flag_is_a_boolean_not_a_string()
    {
        var error = Assert.Throws<FieldValidationException>(() => AssetRules.Parse(Body.Of("""{"includes_personal_information": "yes"}""")));
        Assert.Contains("includes_personal_information", error.Fields.Keys);
        Assert.True(AssetRules.Parse(Body.Of("""{"category": "customer_lists_and_intangibles", "includes_personal_information": true}""")).IncludesPersonalInformation);
    }

    // ── Beyond the Python suite: the wire strings ──────────────────

    [Fact]
    public void Every_body_member_is_named_as_its_parser_reads_it()
    {
        // The stored body's member names must be exactly what the parsers
        // read — a digit in a name is where a naming policy would go wrong.
        foreach (var collection in CaseCollections.ByCollection.Keys)
        {
            var sent = Body.Of(SampleBodies[collection]);
            var stored = BodyJson.Prune(CaseCollections.ByCollection[collection].ParseDraft(sent, enforceProvenance: false).BodyNode());
            foreach (var (key, _) in sent)
            {
                Assert.True(stored.ContainsKey(key), $"{collection}.{key}");
            }
        }
    }
}
