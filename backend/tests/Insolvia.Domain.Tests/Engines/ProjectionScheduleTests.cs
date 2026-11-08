using Insolvia.Core;
using Insolvia.Core.Assets;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Core.Debtors;
using Insolvia.Core.Exemptions;
using Insolvia.Core.Fields;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Tests.Support;
using static Insolvia.Engines.Tests.Support.Projected;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_form_projections.py — the
// targeted mappings for B101 and Schedules A/B, C, D, E/F, G and B108, over
// the same reference case (Parity/reference-ch7.case.json). The file's
// PDF-readback goldens are replaced by ParityTests: the projected values
// themselves, diffed against the Python's.
public sealed class ProjectionScheduleTests
{
    private static CaseFile Reference => CaseFiles.ReferenceCh7;

    private static Debtor Debtor1 => Reference.Debtors[0];

    private static Debtor Debtor2 => Reference.Debtors[1];

    // --- B101 ---

    [Fact]
    public void Line_15_uses_the_verified_exports()
    {
        var values = Project("form/b101", Reference);
        Assert.Equal(Option("1"), values["line_15_debtor1_credit_counseling"]);
        Assert.Equal(Option("4"), values["line_15_debtor2_credit_counseling"]);
        Assert.Equal(Option("Disability"), values["line_15_debtor2_exemption_reason"]);
    }

    [Fact]
    public void Line_16_translates_the_three_way_into_two_gates()
    {
        var values = Project("form/b101", Reference);
        Assert.Equal(Option("Yes"), values["line_16a_consumer_debts"]);
        Assert.False(values.ContainsKey("line_16b_business_debts"));
        Assert.False(values.ContainsKey("line_16c_other_debts"));
    }

    [Fact]
    public void Line_19_picks_the_misprinted_band_by_position()
    {
        var values = Project("form/b101", Reference);
        Assert.Equal(Option("100000000-500000000"), values["line_19_estimated_assets"]);
        Assert.Equal(Option("50001-100000"), values["line_20_estimated_liabilities"]);
    }

    [Fact]
    public void Line_17_answers_from_the_chapter()
    {
        var values = Project("form/b101", Reference);
        Assert.Equal(Option("Yes"), values["line_17_filing_under_ch7"]);
        Assert.Equal(Option("No"), values["line_17_funds_available"]);
    }

    [Fact]
    public void The_attorney_block_wins_over_the_pro_se_block()
    {
        var values = Project("form/b101", Reference);
        Assert.Equal(Text("Alex Counsel"), values["attorney.printed_name"]);
        Assert.Equal(Text("08/31/2026"), values["attorney.date_signed"]);
        Assert.DoesNotContain(values.Keys, key => key.StartsWith("prose.", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_an_attorney_the_pro_se_block_carries_contacts()
    {
        var values = Project("form/b101", Reference with { FilingProfessionals = [] });
        Assert.Equal(Option("no"), values["prose.paid_preparer"]);
        Assert.Equal(Text("(813) 555-0101"), values["prose.debtor1_phone"]);
        Assert.Equal(Text("08/30/2026"), values["prose.debtor1_date"]);
        Assert.DoesNotContain(values.Keys, key => key.StartsWith("attorney.", StringComparison.Ordinal));
    }

    [Fact]
    public void B101_prints_the_last_four_in_the_box_for_the_kind()
    {
        var values = Project("form/b101", Reference);
        Assert.Equal(Text(Debtor1.TaxId!.LastFour), values[$"line_3_debtor1_{Debtor1.TaxId.Kind.ToWire()}_last4"]);
        Assert.Equal(Text(Debtor2.TaxId!.LastFour), values[$"line_3_debtor2_{Debtor2.TaxId.Kind.ToWire()}_last4"]);
    }

    [Fact]
    public void Facts_that_do_not_fit_the_printed_rows_are_errors()
    {
        var crowded = Debtor1 with { Body = Debtor1.Body with { OtherNamesUsed = Values(new OtherName("a1", Surname: "Byron"), new OtherName("a2", Surname: "King"), new OtherName("a3", Surname: "Noel")) } };
        Assert.Contains("prints 2 rows", Refused("form/b101", Reference with { Debtors = [crowded] }).Message, StringComparison.Ordinal);

        var doubled = Reference with { SoleProprietorships = [.. Reference.SoleProprietorships, .. Reference.SoleProprietorships] };
        Assert.Contains("one sole-proprietorship block", Refused("form/b101", doubled).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmapped_revision_is_refused()
    {
        var release = FormTemplates.Get("form/b101", "form/b101@2024-06-22");
        var unknown = release with { EffectiveDate = new DateOnly(2030, release.EffectiveDate.Month, release.EffectiveDate.Day) };
        Assert.Contains("no projection is written", Assert.Throws<EngineKeyException>(() => FormProjections.Project(unknown, Reference)).Message, StringComparison.Ordinal);
    }

    // --- B106I (the arithmetic lines) ---

    [Fact]
    public void B106i_derives_the_arithmetic_lines()
    {
        var values = Project("form/b106i", Reference);
        FieldFill Column(string fieldId, string digit) =>
            Assert.IsType<RowFills>(values[fieldId]).First(pair => pair.Key.Contains($"Debtor {digit}", StringComparison.Ordinal) || pair.Key.Contains($"debtor {digit}", StringComparison.Ordinal)).Value;

        Assert.Equal(Text("5,450.00"), Column("line_4_gross_income", "1"));
        Assert.Equal(Text("950.50"), Column("line_6_total_deductions", "1"));
        Assert.Equal(Text("4,499.50"), Column("line_7_take_home_pay", "1"));
        Assert.Equal(Text("400.00"), Column("line_9_total_other_income", "1"));
        Assert.Equal(Text("4,899.50"), Column("line_10_monthly_income", "1"));
        Assert.Equal(Text("3,000.00"), Column("line_10_monthly_income", "2"));
        Assert.Equal(Text("7,899.50"), values["line_10_combined"]);
        Assert.Equal(Text("250.00"), values["line_11_household_contributions"]);
        Assert.Equal(Text("8,149.50"), values["line_12_combined_monthly_income"]);
        Assert.Equal(Text("5,450.00"), Column("line_4_copy", "1"));
    }

    [Fact]
    public void B106i_second_column_takes_a_non_filing_spouse()
    {
        var spouse = Debtor2 with { FilingRole = FilingRole.NonFilingSpouse };
        Assert.Equal(Text("Ben Lovelace Jr."), Project("form/b106i", Reference with { Debtors = [Debtor1, spouse] })["caption.debtor2_name"]);
    }

    // --- B106A/B ---

    [Fact]
    public void B106ab_derives_the_part_totals_and_the_rollup()
    {
        var values = Project("form/b106ab", Reference);
        Assert.Equal(Text("240,000.00"), values["line_2_part1_total"]);
        Assert.Equal(Text("12,500.00"), values["line_5_part2_total"]);
        Assert.Equal(Text("5,300.00"), values["line_15_part3_total"]);
        Assert.Equal(Text("52,800.00"), values["line_36_part4_total"]);
        Assert.Equal(Text("1,950.00"), values["line_45_part5_total"]);
        Assert.Equal(Text("0.00"), values["line_52_part6_total"]);
        Assert.Equal(Text("240,000.00"), values["line_55_total"]);
        Assert.Equal(Text("72,550.00"), values["line_62_total_personal_property"]);
        Assert.Equal(Text("312,550.00"), values["line_63_total_all_property"]);
    }

    [Fact]
    public void B106ab_lands_the_homestead_on_row_one()
    {
        var values = Project("form/b106ab", Reference);
        Assert.Equal(Text("12 Byron Court, Tampa, FL 33601"), Row(values, "form/b106ab", "real_estate.street", 0));
        Assert.Equal(Text("Hillsborough"), Row(values, "form/b106ab", "real_estate.county", 0));
        Assert.Equal(Text("240,000.00"), Row(values, "form/b106ab", "real_estate.value_portion", 0));
        Assert.Equal(Option("Debtor 1 and 2"), Row(values, "form/b106ab", "real_estate.who_has_interest", 0));
    }

    [Fact]
    public void B106ab_vehicle_structured_fields_land_in_their_own_boxes()
    {
        var values = Project("form/b106ab", Reference);
        Assert.Equal(Text("2016"), Row(values, "form/b106ab", "vehicle.year", 0));
        Assert.Equal(Text("Honda"), Row(values, "form/b106ab", "vehicle.make", 0));
        Assert.Equal(Text("Civic LX"), Row(values, "form/b106ab", "vehicle.model", 0));
        Assert.Equal(Text("92000"), Row(values, "form/b106ab", "vehicle.mileage", 0));
        Assert.Equal(Text("One owner; always garaged"), Row(values, "form/b106ab", "vehicle.other_information", 0));
        Assert.Equal(Option("Debtor 1"), Row(values, "form/b106ab", "vehicle.who_has_interest", 0));
    }

    [Fact]
    public void B106ab_other_vehicle_has_no_mileage_box()
    {
        var values = Project("form/b106ab", Reference);
        Assert.Equal(Text("2005"), Row(values, "form/b106ab", "other_vehicle.year", 0));
        Assert.Equal(Text("Sun Tracker"), Row(values, "form/b106ab", "other_vehicle.make", 0));
        Assert.Equal(Text("Pontoon boat"), Row(values, "form/b106ab", "other_vehicle.model", 0));
        Assert.False(values.ContainsKey("other_vehicle.mileage"));
    }

    [Fact]
    public void B106ab_single_box_lines_aggregate_their_category()
    {
        var values = Project("form/b106ab", Reference);
        Assert.Equal(Option("yes"), values["line_6_gate"]);
        Assert.Equal(Text("Used furniture and kitchen appliances"), values["line_6_description"]);
        Assert.Equal(Text("2,200.00"), values["line_6_amount"]);
        Assert.Equal(Option("no"), values["line_8_gate"]);
        Assert.False(values.ContainsKey("line_8_amount"));
    }

    [Fact]
    public void B106ab_line_43_pii_gate_answers_yes_when_any_list_carries_it()
    {
        var values = Project("form/b106ab", Reference);
        Assert.Equal(Option("yes"), values["line_43_gate"]);
        Assert.Equal(Option("yes"), values["line_43_pii_gate"]);

        values = Project("form/b106ab", Reference.WithAsset("asset-customer-list", body => body with { IncludesPersonalInformation = false }));
        Assert.Equal(Option("no"), values["line_43_pii_gate"]);

        values = Project("form/b106ab", Reference with { Assets = [.. Reference.Assets.Where(pair => pair.Id != "asset-customer-list")] });
        Assert.Equal(Option("no"), values["line_43_gate"]);
        Assert.False(values.ContainsKey("line_43_pii_gate"));
    }

    [Fact]
    public void B106ab_deposits_take_one_printed_row_each()
    {
        var values = Project("form/b106ab", Reference);
        Assert.Equal(Text("Checking account, Suncoast Credit Union"), Row(values, "form/b106ab", "line_17_institution", 0));
        Assert.Equal(Text("2,100.00"), Row(values, "form/b106ab", "line_17_amount", 1));
    }

    [Fact]
    public void B106ab_routes_line_28_amounts_by_detail_keyword()
    {
        var values = Project("form/b106ab", Reference);
        Assert.Equal(Option("On"), values["line_28_gate"]);
        Assert.Equal(Text("1,100.00"), values["line_28_amount_federal"]);
        Assert.False(values.ContainsKey("line_28_amount_state"));

        var mystery = ("asset-mystery", Build.Body(AssetRules.Kind, """{"category": "money_owed_to_you", "description": "A refund of some kind", "detail": "unspecified", "value_portion_owned": "10.00"}"""));
        var error = Refused("form/b106ab", Reference with { Assets = [.. Reference.Assets, mystery] });
        Assert.Contains("names none of them", error.Message, StringComparison.Ordinal);
        Assert.Contains("'unspecified'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B106ab_overflow_past_the_printed_rows_is_an_error()
    {
        var extra = Enumerable.Range(0, 3).Select(n => ($"asset-lot-{n}", Build.Body(AssetRules.Kind, $$"""{"category": "real_property", "description": "Vacant lot {{n}}", "value_portion_owned": "1000.00"}""")));
        Assert.Contains("prints 3 rows", Refused("form/b106ab", Reference with { Assets = [.. Reference.Assets, .. extra] }).Message, StringComparison.Ordinal);
    }

    // --- B106C ---

    [Fact]
    public void B106c_line_1_is_forced_by_the_opt_out_rule() =>
        Assert.Equal(Option("state and federal"), Project("form/b106c", Reference)["line_1_exemption_set"]);

    [Theory]
    [InlineData(ExemptionSet.Federal, "federal")]
    [InlineData(ExemptionSet.StateAndFederalNonbankruptcy, "state and federal")]
    public void B106c_line_1_prints_the_cases_election_where_the_debtor_may_elect(ExemptionSet set, string export) =>
        Assert.Equal(Option(export), Project("form/b106c", Texan(set))["line_1_exemption_set"]);

    [Fact]
    public void B106c_line_1_stays_blank_where_the_debtor_may_elect() =>
        Assert.False(Project("form/b106c", Texan(null)).ContainsKey("line_1_exemption_set"));

    [Fact]
    public void B106c_rows_copy_the_asset_and_spell_the_election()
    {
        var values = Project("form/b106c", Reference);
        Assert.Equal(Text("12 Byron Court, Tampa, FL 33601"), Row(values, "form/b106c", "line_2_property_description", 0));
        Assert.Equal(Text("240,000.00"), Row(values, "form/b106c", "line_2_current_value", 0));
        Assert.Equal(Option("fair market"), Row(values, "form/b106c", "line_2_exemption_kind", 0));
        Assert.False(HasRow(values, "form/b106c", "line_2_exemption_amount", 0));
        Assert.Equal(Option("On"), Row(values, "form/b106c", "line_2_exemption_kind", 1));
        Assert.Equal(Text("5,000.00"), Row(values, "form/b106c", "line_2_exemption_amount", 1));
        Assert.Equal(Text("Fla. Stat. § 222.25(1)"), Row(values, "form/b106c", "line_2_statute_citation", 1));
    }

    [Fact]
    public void B106c_answers_the_homestead_cap_from_the_registry()
    {
        var values = Project("form/b106c", Reference);
        Assert.Equal(Option("yes"), values["line_3_homestead_over_cap"]);
        Assert.Equal(Option("no"), values["line_3_acquired_within_1215_days"]);
    }

    [Fact]
    public void B106c_under_cap_answers_no_and_skips_the_follow_up()
    {
        var values = Project("form/b106c", Reference.WithAsset("asset-house", body => body with { ValuePortionOwned = 180000.00m }));
        Assert.Equal(Option("no"), values["line_3_homestead_over_cap"]);
        Assert.False(values.ContainsKey("line_3_acquired_within_1215_days"));
    }

    [Fact]
    public void B106c_overflow_past_fifteen_rows_is_an_error()
    {
        var crowded = Reference.Exemptions.Concat(Enumerable.Repeat(new ExemptionBody("asset-clothes", "Fla. test", null, null, null), 12)).ToList();
        Assert.Contains("prints 15 rows", Refused("form/b106c", Reference with { Exemptions = crowded }).Message, StringComparison.Ordinal);
    }

    // --- B106D ---

    [Fact]
    public void B106d_rows_resolve_their_creditor_and_derive_the_unsecured_portion()
    {
        var values = Project("form/b106d", Reference);
        Assert.Equal(Text("Gulf Coast Home Loans"), Row(values, "form/b106d", "claim.creditor_name", 0));
        Assert.Equal(Text("195,000.00"), Row(values, "form/b106d", "claim.amount", 0));
        Assert.Equal(Text("240,000.00"), Row(values, "form/b106d", "claim.collateral_value", 0));
        Assert.Equal(Text("0.00"), Row(values, "form/b106d", "claim.unsecured_portion", 0));
        Assert.Equal(Option("Debtor 1 and 2"), Row(values, "form/b106d", "claim.who_owes", 0));
        Assert.Equal(Text("202,400.00"), Row(values, "form/b106d", "part1_page_subtotal", 0));
        Assert.Equal(1, Assert.IsType<RowFills>(values["part1_page_subtotal"]).Count);
        Assert.Equal(Text("202,400.00"), values["part1_total"]);
    }

    [Fact]
    public void B106d_underwater_collateral_leaves_an_unsecured_portion() =>
        Assert.Equal(Text("15,000.00"), Row(Project("form/b106d", Reference.WithClaim("claim-mortgage", body => body with { CollateralValue = 180000.00m })), "form/b106d", "claim.unsecured_portion", 0));

    [Fact]
    public void B106d_collateral_columns_resolve_through_the_asset()
    {
        var values = Project("form/b106d", Reference);
        Assert.Equal(Text("2016 Honda Civic LX"), Row(values, "form/b106d", "claim.collateral_description", 1));
        Assert.Equal(Text("9,000.00"), Row(values, "form/b106d", "claim.collateral_value", 1));
        Assert.Equal(Text("0.00"), Row(values, "form/b106d", "claim.unsecured_portion", 1));
    }

    [Fact]
    public void B106d_senior_liens_and_the_override_reach_column_b()
    {
        var junior = Build.Body(ClaimRules.Kind, """{"creditor_id": "cred-auto", "claim_class": "secured", "amount": "100000.00", "asset_id": "asset-house", "lien_position": 2, "lien_nature": ["agreement"]}""");
        var manual = Build.Body(ClaimRules.Kind, """{"creditor_id": "cred-auto", "claim_class": "secured", "amount": "5000.00", "collateral_value": "1.00", "unsecured_amount_override": "4321.00"}""");
        var values = Project("form/b106d", Reference with { Claims = [.. Reference.Claims, ("claim-second", junior), ("claim-manual", manual)] });
        Assert.Equal(Text("265,000.00"), Row(values, "form/b106d", "claim.collateral_value", 2));
        Assert.Equal(Text("30,000.00"), Row(values, "form/b106d", "claim.unsecured_portion", 2));
        Assert.Equal(Text("4,321.00"), Row(values, "form/b106d", "claim.unsecured_portion", 3));
    }

    [Fact]
    public void B106d_broken_who_owes_rows_use_the_widget_escape_hatches()
    {
        var filler = Enumerable.Range(0, 3).Select(n => ($"claim-extra-{n}", Build.Body(ClaimRules.Kind, $$"""{"creditor_id": "cred-auto", "claim_class": "secured", "amount": "100.00", "who_incurred": "{{(n == 1 ? "at_least_one_plus_another" : "debtor_2")}}"}""")));
        var values = Project("form/b106d", Reference with { Claims = [.. Reference.Claims, .. filler] });
        Assert.Equal(WidgetStatesFill.ByIndexes(3), values["claim.who_owes_row_2_4"]);
        Assert.Equal(["Debtor 2 only_5"], Assert.IsType<RowFills>(values["claim.who_owes_row_2_5"]).Select(pair => pair.Key));
    }

    [Fact]
    public void B106d_notice_row_four_can_only_mirror_claim_one()
    {
        var parties = Values([.. Enumerable.Range(0, 4).Select(n => new NoticeParty($"np-{n}", $"Notice Party {n}", null, $"000{n}"))]);
        Assert.Contains("second widget", Refused("form/b106d", Reference.WithClaim("claim-mortgage", body => body with { NoticeParties = parties })).Message, StringComparison.Ordinal);
    }

    // --- B106E/F ---

    [Fact]
    public void B106ef_derives_each_priority_total_and_the_part_4_rollup()
    {
        var values = Project("form/b106ef", Reference);
        Assert.Equal(Text("3,650.00"), Row(values, "form/b106ef", "priority.total_claim", 0));
        Assert.Equal(Text("3,200.00"), Row(values, "form/b106ef", "priority.priority_amount", 0));
        Assert.Equal(Text("450.00"), Row(values, "form/b106ef", "priority.nonpriority_amount", 0));
        Assert.Equal(Text("3,650.00"), values["line_6b_total"]);
        Assert.Equal(Text("3,650.00"), values["line_6e_total"]);
        Assert.Equal(Text("12,000.00"), values["line_6f_total"]);
        Assert.Equal(Text("10,800.00"), values["line_6i_total"]);
        Assert.Equal(Text("22,800.00"), values["line_6j_total"]);
        Assert.Equal(Text("0.00"), values["line_6a_total"]);
    }

    [Fact]
    public void B106ef_types_and_offsets_land_per_row()
    {
        var values = Project("form/b106ef", Reference);
        Assert.Equal(new CheckFill(), Row(values, "form/b106ef", "priority.type_taxes", 0));
        Assert.Equal(Text("Credit card purchases"), Row(values, "form/b106ef", "nonpriority.type_other_specify", 0));
        Assert.Equal(Option("yes"), Row(values, "form/b106ef", "nonpriority.subject_to_offset", 1));
        Assert.Equal(new CheckFill(), Row(values, "form/b106ef", "nonpriority.type_student_loans", 2));
        Assert.Equal(Text("12,000.00"), Row(values, "form/b106ef", "nonpriority.amount", 2));
    }

    [Fact]
    public void B106ef_notice_rows_answer_which_part_from_the_claims_class()
    {
        var values = Project("form/b106ef", Reference);
        Assert.Equal(Text("Meridian Recovery Services"), Row(values, "form/b106ef", "notify.name", 0));
        Assert.Equal(Option("yes"), Row(values, "form/b106ef", "notify.referenced_part", 0));
    }

    // --- B106G ---

    [Fact]
    public void B106g_contracts_take_one_printed_row_each()
    {
        var values = Project("form/b106g", Reference);
        Assert.Equal(Option("yes"), values["line_1_any_contracts"]);
        Assert.Equal(Text("StorSafe Tampa LLC"), Row(values, "form/b106g", "line_2_counterparty_name", 0));
        Assert.Equal(Text("Two-year wireless service agreement, 14 months remaining"), Row(values, "form/b106g", "line_2_contract_description", 1));
    }

    // --- B108 ---

    [Fact]
    public void B108_part_1_rows_the_secured_claims_with_their_intentions()
    {
        var values = Project("form/b108", Reference);
        Assert.Equal(Text("Gulf Coast Home Loans"), Row(values, "form/b108", "line_1_creditor_name", 0));
        Assert.Equal(Text("12 Byron Court, Tampa, FL 33601"), Row(values, "form/b108", "line_1_property_description", 0));
        Assert.Equal(Option("retain and reaffirmation"), Row(values, "form/b108", "line_1_intention", 0));
        Assert.Equal(Text("2016 Honda Civic LX"), Row(values, "form/b108", "line_1_property_description", 1));
        Assert.Equal(Option("retain and redeem"), Row(values, "form/b108", "line_1_intention", 1));
        Assert.Equal(Option("yes"), Row(values, "form/b108", "line_1_claimed_exempt", 0));
        Assert.Equal(Option("yes"), Row(values, "form/b108", "line_1_claimed_exempt", 1));
        Assert.False(Assert.IsType<RowFills>(values["line_1_intention"]).ContainsKey("check1 1c"));
        Assert.False(values.ContainsKey("line_1_intention_explanation_line1"));
    }

    [Fact]
    public void B108_part_2_prints_only_the_leases_flagged_for_the_statement()
    {
        var values = Project("form/b108", Reference);
        Assert.Equal(Text("Sunshine Auto Leasing LLC"), Row(values, "form/b108", "line_2_lessor_name", 0));
        Assert.Equal(Option("yes"), Row(values, "form/b108", "line_2_assumed", 0));
        Assert.Equal(1, Assert.IsType<RowFills>(values["line_2_lessor_name"]).Count);
        Assert.Equal(Text("08/30/2026"), values["debtor1_signature_date"]);
    }

    [Fact]
    public void B108_retain_other_wraps_its_explanation_onto_the_two_lines()
    {
        var values = Project("form/b108", Reference.WithClaim("claim-auto", body => body with { Intention = ClaimIntention.RetainOther, IntentionExplanation = "Keep paying under the current terms" }));
        Assert.Equal(Option("retain and explain"), Row(values, "form/b108", "line_1_intention", 1));
        Assert.Equal(Text("Keep"), Row(values, "form/b108", "line_1_intention_explanation_line1", 1));
        Assert.Equal(Text("paying under the current terms"), Row(values, "form/b108", "line_1_intention_explanation_line2", 1));
    }

    [Fact]
    public void B108_an_explanation_past_the_two_lines_is_an_error() =>
        Assert.Contains(
            "does not fit the two printed",
            Refused("form/b108", Reference.WithClaim("claim-auto", body => body with { Intention = ClaimIntention.RetainOther, IntentionExplanation = string.Concat(Enumerable.Repeat("x ", 40)) })).Message,
            StringComparison.Ordinal);

    [Fact]
    public void B108_claimed_exempt_answers_no_without_a_schedule_c_claim() =>
        Assert.Equal(Option("no"), Row(Project("form/b108", Reference with { Exemptions = [] }), "form/b108", "line_1_claimed_exempt", 0));

    private static CaseFile Texan(ExemptionSet? set)
    {
        var texan = Debtor1 with { Body = Debtor1.Body with { ResidenceAddress = new Address("1 Alamo Plaza", City: "San Antonio", State: "TX", PostalCode: "78205") } };
        return Reference with { Case = Reference.Case with { ExemptionSet = set }, Debtors = [texan, Debtor2] };
    }
}
