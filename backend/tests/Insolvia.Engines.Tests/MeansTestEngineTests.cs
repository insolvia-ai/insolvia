using System.Text.Json.Nodes;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;
using Insolvia.Core.Income;
using Insolvia.Core.MeansTest;
using Insolvia.Engines.MeansTest;
using Insolvia.Engines.Tests.Support;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_means_test.py: known-answer
// checks for the means-test engine — the full Gainesville trace, all four ways
// the Chapter 7 determination settles, and both Chapter 13 comparisons.
public sealed class MeansTestEngineTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 4);
    private static readonly MeansTestData Data = MeansTestEngine.ResolveData(AsOf);

    // ── the median comparison ──

    [Fact]
    public void The_comparison_annualizes_and_reads_the_dated_table()
    {
        var comparison = MeansTestEngine.Compare("4000.00", "fl", 1, Data);
        Assert.Equal("48000.00", comparison.AnnualizedCmi);
        Assert.Equal("69876.00", comparison.AnnualMedian);
        Assert.False(comparison.AboveMedian);
        Assert.Contains("ust/census-median-family-income@2026-04-01", comparison.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Income_equal_to_the_median_is_not_above_it()
    {
        var comparison = MeansTestEngine.Compare("7210.25", "FL", 2, Data);
        Assert.Equal("86523.00", comparison.AnnualizedCmi);
        Assert.False(comparison.AboveMedian);
    }

    [Fact]
    public void A_household_above_four_adds_the_statutory_amount() =>
        Assert.Equal("140162.00", MeansTestEngine.Compare("12000.00", "TX", 6, Data).AnnualMedian);

    [Fact]
    public void An_unknown_jurisdiction_refuses() =>
        Assert.Contains("no row", Assert.Throws<MeansTestException>(() => MeansTestEngine.Compare("4000.00", "ZZ", 2, Data)).Message, StringComparison.Ordinal);

    // ── outcomes ──

    [Fact]
    public void A_below_median_debtor_stops_at_the_comparison()
    {
        var result = Run(CaseFor("4000.00", Household()));
        Assert.Equal("below_median", result.Outcome);
        Assert.Equal("median", result.DeterminedBy);
        Assert.Empty(result.Lines);
        Assert.Equal("ust/census-median-family-income@2026-04-01", result.ReleaseIds["ust/census-median-family-income"]);
    }

    [Fact]
    public void High_disposable_income_presumes_abuse_at_the_ceiling()
    {
        var result = Run(CaseFor("9000.00", Household(under65: 1)));
        Assert.Equal("presumption_of_abuse", result.Outcome);
        Assert.Equal("threshold_ceiling", result.DeterminedBy);
        Assert.Contains("17150.00", Line(result, "40").Source, StringComparison.Ordinal);
        Assert.Contains("§ 707(b)(2)(A)(i)(II)", Line(result, "40").Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_middle_band_compares_against_a_quarter_of_unsecured_debt()
    {
        var inputs = Household(under65: 1, extra: """{"taxes": "2000.00", "health_insurance": "760.00"}""");
        var presumed = Run(CaseFor("6000.00", inputs, nonpriority: "40000.00"));
        Assert.Equal("15000.00", Line(presumed, "39d").Amount);
        Assert.Equal("10000.00", Line(presumed, "41b").Amount);
        Assert.Equal("presumption_of_abuse", presumed.Outcome);
        Assert.Equal("unsecured_ratio", presumed.DeterminedBy);

        var cleared = Run(CaseFor("6000.00", inputs, nonpriority: "80000.00"));
        Assert.Equal("20000.00", Line(cleared, "41b").Amount);
        Assert.Equal("no_presumption", cleared.Outcome);
        Assert.Equal("unsecured_ratio", cleared.DeterminedBy);
    }

    // ── the full trace, hand-computed ──

    [Fact]
    public void The_full_calculation_line_by_line()
    {
        var result = Run(FullCase());
        Assert.True(result.Comparison!.AboveMedian);
        var expected = new Dictionary<string, string>
        {
            ["1"] = "7500.00", ["3"] = "0.00", ["4"] = "7500.00", ["5"] = "2.00", ["6"] = "1558.00", ["7"] = "180.00",
            ["8"] = "690.00", ["9a"] = "1440.00", ["9b"] = "1750.00", ["9c"] = "0.00", ["12"] = "291.00", ["13a"] = "703.00",
            ["13b"] = "450.00", ["13c"] = "253.00", ["13"] = "253.00", ["14"] = "0.00", ["16"] = "1500.00", ["24"] = "4792.00",
            ["25"] = "450.00", ["29"] = "200.00", ["30"] = "50.00", ["32"] = "725.00", ["33e"] = "2200.00", ["34"] = "50.00",
            ["35"] = "100.00", ["36"] = "50.00", ["37"] = "2400.00", ["38"] = "7917.00", ["39c"] = "-417.00", ["39d"] = "-25020.00",
        };
        foreach (var (number, amount) in expected)
        {
            Assert.Equal(amount, Line(result, number).Amount);
        }

        Assert.Equal("no_presumption", result.Outcome);
        Assert.Equal("threshold_floor", result.DeterminedBy);
    }

    [Fact]
    public void Every_figure_names_its_rule_input_or_dataset()
    {
        var result = Run(FullCase());
        Assert.All(result.Lines, entry => Assert.False(string.IsNullOrEmpty(entry.Source)));
        Assert.Contains("ust/irs-national-standards@2026-07-15", Line(result, "6").Source, StringComparison.Ordinal);
        Assert.Contains("ust/irs-local-standards@2026-07-15", Line(result, "8").Source, StringComparison.Ordinal);
        Assert.Contains("ust/ch13-admin-multipliers@2026-07-15", Line(result, "36").Source, StringComparison.Ordinal);
        Assert.Contains("code/dollar-amounts@2025-04-01", Line(result, "29").Source, StringComparison.Ordinal);
        Assert.Contains("means_test_input.taxes", Line(result, "16").Source, StringComparison.Ordinal);
        Assert.StartsWith("lines 6 + 7 + 8", Line(result, "24").Source, StringComparison.Ordinal);
        Assert.Equal(
            new HashSet<string> { "ust/census-median-family-income", "ust/irs-national-standards", "ust/irs-local-standards", "ust/ch13-admin-multipliers", "code/dollar-amounts" },
            result.ReleaseIds.Keys.ToHashSet());
    }

    [Fact]
    public void The_cmi_engine_composes_into_the_means_test()
    {
        var cmi = PaidCmi("d-1", FilingRole.Debtor1, 7500.00m);
        Assert.Equal("7500.00", cmi.CombinedMonthlyTotal);
        var result = Run(CaseFor("unused", FullInputs(), children: 1, cmi: cmi));
        Assert.Equal("no_presumption", result.Outcome);
    }

    // ── refusals ──

    [Fact]
    public void Missing_household_composition_refuses() =>
        Assert.Contains("household composition", Assert.Throws<MeansTestException>(() => Run(CaseFor("9000.00", Inputs("{}")))).Message, StringComparison.Ordinal);

    [Fact]
    public void An_unknown_county_refuses_rather_than_guessing() =>
        Assert.Contains("no housing standard", Assert.Throws<MeansTestException>(() => Run(CaseFor("9000.00", Household(), county: "Nowhere County"))).Message, StringComparison.Ordinal);

    [Fact]
    public void An_unknown_district_refuses_when_ch13_applies()
    {
        var inputs = Household(under65: 1, extra: """{"ch13_eligible": true, "ch13_projected_plan_payment": "500.00"}""");
        Assert.Contains("no district", Assert.Throws<MeansTestException>(() => Run(CaseFor("9000.00", inputs, district: "Outer Nowhere"))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Statutory_caps_refuse_rather_than_clamp()
    {
        var overEducation = CaseFor("9000.00", Household(under65: 1, extra: """{"education_under_18": "500.00"}"""), children: 1);
        Assert.Contains("707(b)(2)(A)(ii)(IV)", Assert.Throws<MeansTestException>(() => Run(overEducation)).Message, StringComparison.Ordinal);

        var overFood = CaseFor("9000.00", Household(under65: 1, extra: """{"additional_food_clothing": "100.00"}"""));
        Assert.Contains("5% food-and-clothing cap", Assert.Throws<MeansTestException>(() => Run(overFood)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_marital_adjustment_without_column_b_is_an_error()
    {
        var inputs = Household(under65: 1, extra: """{"marital_adjustments": [{"id": "ma1", "description": "Spouse 401(k)", "amount": "300.00"}]}""");
        Assert.Contains("no Column B", Assert.Throws<MeansTestException>(() => Run(CaseFor("9000.00", inputs))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolution_before_the_series_begin_refuses() =>
        Assert.Contains("refusing", Assert.Throws<EngineLookupException>(() => MeansTestEngine.ResolveData(new DateOnly(2026, 3, 31))).Message, StringComparison.Ordinal);

    [Fact]
    public void Marital_adjustment_subtracts_when_column_b_exists()
    {
        var cmi = PaidCmi("d-2", FilingRole.Debtor2, 8000.00m);
        var inputs = Household(under65: 2, extra: """{"marital_adjustments": [{"id": "ma1", "description": "Spouse's own student loan", "amount": "300.00"}]}""");
        var result = Run(CaseFor("unused", inputs, cmi: cmi));
        Assert.Equal("8000.00", Line(result, "1").Amount);
        Assert.Equal("300.00", Line(result, "3").Amount);
        Assert.Equal("7700.00", Line(result, "4").Amount);
        Assert.Contains("Spouse's own student loan", Line(result, "3").Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_vehicles_take_the_per_vehicle_ownership_standard()
    {
        var inputs = Household(under65: 1, extra: """{"vehicle_count": 2, "vehicle_1_loan_monthly": "800.00", "vehicle_2_loan_monthly": "100.00"}""");
        var result = Run(CaseFor("9000.00", inputs, county: "Miami-Dade"));
        Assert.Equal("846.00", Line(result, "12").Amount);
        Assert.Equal("0.00", Line(result, "13c").Amount);
        Assert.Equal("603.00", Line(result, "13f").Amount);
        Assert.Equal("603.00", Line(result, "13").Amount);
    }

    [Fact]
    public void Unused_secured_rows_and_sixty_month_math()
    {
        var inputs = Household(under65: 1, extra: """{"other_secured_payments": [{"id": "os1", "creditor_name": "Marina Finance", "property_description": "Boat", "monthly_payment": "120.00"}]}""");
        Assert.IsType<OtherSecuredPayment>(inputs.OtherSecuredPayments[0]);
        var result = Run(CaseFor("9000.00", inputs, priority: "9001.00"));
        Assert.Equal("120.00", Line(result, "33d").Amount);
        Assert.Contains("Marina Finance", Line(result, "33d").Source, StringComparison.Ordinal);
        Assert.Equal("150.02", Line(result, "35").Amount);
    }

    // ── issue #349: the screen's inputs ──

    [Theory]
    [InlineData("non_consumer_debts")]
    [InlineData("disabled_veteran")]
    [InlineData("reservist_national_guard")]
    public void An_exemption_ends_the_test_before_any_line(string flag)
    {
        var result = Run(CaseFor("9000.00", Household(extra: $$"""{"{{flag}}": true}""")));
        Assert.Equal("exempt", result.Outcome);
        Assert.Equal(flag, result.DeterminedBy);
        Assert.Empty(result.Lines);
        Assert.NotNull(result.Comparison);
        Assert.True(result.Comparison.AboveMedian);
    }

    [Fact]
    public void An_exempt_debtor_needs_no_household_at_all()
    {
        var result = Run(CaseFor("9000.00", Inputs("""{"non_consumer_debts": true}""")));
        Assert.Equal("exempt", result.Outcome);
        Assert.Null(result.Comparison);
    }

    [Fact]
    public void A_declined_exemption_is_not_an_exemption() =>
        Assert.Equal("below_median", Run(CaseFor("4000.00", Household(extra: """{"disabled_veteran": false}"""))).Outcome);

    [Fact]
    public void The_median_household_size_override_wins_the_comparison()
    {
        var result = Run(CaseFor("8000.00", Household(under65: 2, extra: """{"median_household_size": 4}""")));
        Assert.Equal(4, result.Comparison!.HouseholdSize);
        Assert.Equal("below_median", result.Outcome);
    }

    [Fact]
    public void The_median_size_alone_decides_a_below_median_case() =>
        Assert.Equal("below_median", Run(CaseFor("4000.00", Inputs("""{"median_household_size": 4}"""))).Outcome);

    [Fact]
    public void Above_the_median_the_age_bands_are_still_required() =>
        Assert.Contains("age bands", Assert.Throws<MeansTestException>(() => Run(CaseFor("9000.00", Inputs("""{"median_household_size": 1}""")))).Message, StringComparison.Ordinal);

    [Fact]
    public void The_irs_family_size_drives_lines_5_and_6_only()
    {
        var baseline = Run(CaseFor("9000.00", Household(under65: 2)));
        var sized = Run(CaseFor("9000.00", Household(under65: 2, extra: """{"irs_family_size": 3}""")));
        Assert.Equal("3.00", Line(sized, "5").Amount);
        Assert.Contains("irs_family_size", Line(sized, "5").Source, StringComparison.Ordinal);
        Assert.NotEqual(Line(baseline, "6").Amount, Line(sized, "6").Amount);
        Assert.Contains("household of 3", Line(sized, "6").Source, StringComparison.Ordinal);
        Assert.Equal(2, sized.Comparison!.HouseholdSize);
        Assert.Equal(Line(baseline, "8").Amount, Line(sized, "8").Amount);
    }

    [Fact]
    public void The_housing_family_size_drives_lines_8_and_9a_only()
    {
        var baseline = Run(CaseFor("9000.00", Household(under65: 2)));
        var sized = Run(CaseFor("9000.00", Household(under65: 2, extra: """{"irs_housing_family_size": 4}""")));
        Assert.NotEqual(Line(baseline, "8").Amount, Line(sized, "8").Amount);
        Assert.Contains("household of 4", Line(sized, "8").Source, StringComparison.Ordinal);
        Assert.Contains("irs_housing_family_size", Line(sized, "9a").Source, StringComparison.Ordinal);
        Assert.Equal(Line(baseline, "6").Amount, Line(sized, "6").Amount);
        Assert.Equal("2.00", Line(sized, "5").Amount);
    }

    [Fact]
    public void Bucketed_claim_rows_feed_the_home_and_vehicle_lines()
    {
        var rows = string.Join(", ", SecuredRow("h1", "home", "1200.00", "600.00"), SecuredRow("h2", "home", "300.00"), SecuredRow("v1", "vehicle_1", "400.00", "60.00"), SecuredRow("v2", "vehicle_2", "250.00"), SecuredRow("o1", "other", "95.00"), SecuredRow("legacy", null, "50.00"));
        var result = Run(CaseFor("9000.00", Household(under65: 1, extra: $$"""{"vehicle_count": 2, "other_secured_payments": [{{rows}}]}""")));
        Assert.Equal("1500.00", Line(result, "9b").Amount);
        Assert.Contains("bucket home", Line(result, "9b").Source, StringComparison.Ordinal);
        Assert.Equal("400.00", Line(result, "13b").Amount);
        Assert.Equal("250.00", Line(result, "13e").Amount);
        Assert.Equal("145.00", Line(result, "33d").Amount);
        Assert.Equal("2295.00", Line(result, "33e").Amount);
        Assert.Equal("11.00", Line(result, "34").Amount);
        Assert.Contains("cure_total", Line(result, "34").Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_typed_total_wins_over_the_bucketed_rows_rather_than_adding()
    {
        var row = SecuredRow("h1", "home", "1200.00", "6000.00");
        var result = Run(CaseFor("9000.00", Household(under65: 1, extra: $$"""{"home_secured_monthly_total": "1000.00", "priority_cure_total": "600.00", "other_secured_payments": [{{row}}]}""")));
        Assert.Equal("1000.00", Line(result, "9b").Amount);
        Assert.Contains("home_secured_monthly_total", Line(result, "9b").Source, StringComparison.Ordinal);
        Assert.Equal("10.00", Line(result, "34").Amount);
        Assert.Equal("0.00", Line(result, "33d").Amount);
    }

    // ── issue #365: Chapter 13 ──

    [Fact]
    public void Chapter_7_lines_name_their_form()
    {
        var result = Run(FullCase());
        Assert.Equal(new HashSet<string> { "122A-2" }, result.Lines.Select(entry => entry.Form).ToHashSet());
        Assert.Equal(7, result.Chapter);
        Assert.Null(result.Commitment);
        Assert.Null(result.DisposableIncome);
    }

    [Fact]
    public void A_below_median_chapter_13_debtor_gets_a_three_year_period()
    {
        var result = Run(CaseFor("4000.00", Household()) with { Chapter = 13 });
        Assert.Equal(13, result.Chapter);
        Assert.Equal("below_median", result.Outcome);
        Assert.Equal("median", result.DeterminedBy);
        Assert.Equal("4000.00", result.Comparison!.MonthlyCmi);
        Assert.False(result.Comparison.AboveMedian);
        Assert.Equal(36, result.Commitment!.Months);
        Assert.Equal("48000.00", result.Commitment.AnnualizedIncome);
        Assert.Equal("86523.00", result.Commitment.AnnualMedian);
        Assert.Contains("§ 1325(b)(4)(A)(i)", result.Commitment.Source, StringComparison.Ordinal);
        Assert.Null(result.DisposableIncome);
        Assert.Equal(new HashSet<string> { "122C-1" }, result.Lines.Select(entry => entry.Form).ToHashSet());
        Assert.Equal("0.00", C1(result, "17").Amount);
        Assert.Contains("not determined under § 1325(b)(3)", C1(result, "17").Source, StringComparison.Ordinal);
        Assert.Equal("36.00", C1(result, "21").Amount);
    }

    [Fact]
    public void The_chapter_13_calculation_line_by_line()
    {
        var result = Run(CaseFor("9500.00", Ch13Inputs(), priority: "6000.00", children: 1) with { Chapter = 13 });
        Assert.Equal("above_median", result.Outcome);
        Assert.Equal("median", result.DeterminedBy);
        Assert.Equal(60, result.Commitment!.Months);
        Assert.Equal("9500.00", result.Commitment.MonthlyIncome);
        Assert.Equal("114000.00", result.Commitment.AnnualizedIncome);
        Assert.Contains("§ 1325(b)(4)(A)(ii)", result.Commitment.Source, StringComparison.Ordinal);
        var c1 = new Dictionary<string, string>
        {
            ["11"] = "9500.00", ["12"] = "9500.00", ["13"] = "0.00", ["14"] = "9500.00", ["15a"] = "9500.00", ["15b"] = "114000.00", ["16c"] = "86523.00",
            ["17"] = "1.00", ["18"] = "9500.00", ["19a"] = "0.00", ["19b"] = "9500.00", ["20a"] = "9500.00", ["20b"] = "114000.00", ["20c"] = "86523.00", ["21"] = "60.00",
        };
        foreach (var (number, amount) in c1)
        {
            Assert.Equal(amount, C1(result, number).Amount);
        }

        var c2 = new Dictionary<string, string>
        {
            ["5"] = "2.00", ["6"] = "1558.00", ["24"] = "4792.00", ["32"] = "725.00", ["33e"] = "2200.00", ["34"] = "50.00", ["35"] = "100.00", ["36"] = "50.00",
            ["37"] = "2400.00", ["38"] = "7917.00", ["39"] = "9500.00", ["40"] = "250.00", ["41"] = "150.00", ["42"] = "7917.00", ["43"] = "100.00", ["44"] = "8417.00", ["45"] = "1083.00",
        };
        foreach (var (number, amount) in c2)
        {
            Assert.Equal(amount, C2(result, number).Amount);
        }

        Assert.Equal("1083.00", result.DisposableIncome);
        Assert.Equal(new HashSet<string> { "122C-1", "122C-2" }, result.Lines.Select(entry => entry.Form).ToHashSet());
        Assert.DoesNotContain(result.Lines, entry => entry.Form == "122A-2");
    }

    [Fact]
    public void Every_chapter_13_figure_names_its_rule_input_or_dataset()
    {
        var result = Run(CaseFor("9500.00", Ch13Inputs(), children: 1) with { Chapter = 13 });
        Assert.All(result.Lines, entry => Assert.False(string.IsNullOrEmpty(entry.Source)));
        Assert.Contains("ust/census-median-family-income@2026-04-01", C1(result, "16c").Source, StringComparison.Ordinal);
        Assert.Contains("§ 1325(b)(3)", C1(result, "15b").Source, StringComparison.Ordinal);
        Assert.Contains("§ 1325(b)(4)", C1(result, "20b").Source, StringComparison.Ordinal);
        Assert.Contains("ust/ch13-admin-multipliers@2026-07-15", C2(result, "36").Source, StringComparison.Ordinal);
        Assert.Contains("§ 1325(b)(3)", C2(result, "36").Source, StringComparison.Ordinal);
        Assert.Contains("§ 1325(b)(2)", C2(result, "40").Source, StringComparison.Ordinal);
        Assert.Contains("541(b)(7)", C2(result, "41").Source, StringComparison.Ordinal);
        Assert.Contains("Dialysis travel", C2(result, "43").Source, StringComparison.Ordinal);
        Assert.Contains("§ 707(b)(2)(B)", C2(result, "43").Source, StringComparison.Ordinal);
        Assert.Equal("lines 40 + 41 + 42 + 43", C2(result, "44").Source);
        Assert.Equal("line 39 minus line 44", C2(result, "45").Source);
    }

    [Fact]
    public void The_marital_adjustment_comes_off_before_the_median_on_122c1()
    {
        var inputs = Household(under65: 2, extra: """{"marital_adjustments": [{"id": "ma1", "description": "Spouse's own student loan", "amount": "300.00"}]}""");
        var result = Run(CaseFor("unused", inputs, cmi: PaidCmi("d-s", FilingRole.NonFilingSpouse, 7400.00m)) with { Chapter = 13 });
        Assert.Equal("below_median", result.Outcome);
        Assert.Equal("7100.00", result.Comparison!.MonthlyCmi);
        Assert.Equal("85200.00", result.Comparison.AnnualizedCmi);
        Assert.Equal("300.00", C1(result, "13").Amount);
        Assert.Contains("Spouse's own student loan", C1(result, "13").Source, StringComparison.Ordinal);
        Assert.Equal("0.00", C1(result, "19a").Amount);
        Assert.Contains("not contended", C1(result, "19a").Source, StringComparison.Ordinal);
        Assert.Equal("88800.00", C1(result, "20b").Amount);
        Assert.Equal(60, result.Commitment!.Months);
        Assert.Equal("0.00", result.Commitment.MaritalAdjustment);
    }

    [Fact]
    public void Contending_the_adjustment_for_the_commitment_period_shortens_it()
    {
        var inputs = Household(under65: 2, extra: """{"marital_adjustments": [{"id": "ma1", "description": "Spouse's own student loan", "amount": "300.00"}], "commitment_period_marital_adjustment": true}""");
        var result = Run(CaseFor("unused", inputs, cmi: PaidCmi("d-s", FilingRole.NonFilingSpouse, 7400.00m)) with { Chapter = 13 });
        Assert.Equal("300.00", C1(result, "19a").Amount);
        Assert.Contains("§ 1325(b)(4)", C1(result, "19a").Source, StringComparison.Ordinal);
        Assert.Equal("7100.00", C1(result, "19b").Amount);
        Assert.Equal("85200.00", C1(result, "20b").Amount);
        Assert.Equal(36, result.Commitment!.Months);
        Assert.Equal("300.00", result.Commitment.MaritalAdjustment);
    }

    [Fact]
    public void A_chapter_13_marital_adjustment_without_column_b_is_an_error()
    {
        var inputs = Household(under65: 1, extra: """{"marital_adjustments": [{"id": "ma1", "description": "x", "amount": "300.00"}]}""");
        Assert.Contains("Form 122C-1 has no Column B", Assert.Throws<MeansTestException>(() => Run(CaseFor("4000.00", inputs) with { Chapter = 13 })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chapter_13_form_multiplies_line_36_without_the_eligibility_question()
    {
        var inputs = Household(under65: 1);
        var result = Run(CaseFor("9000.00", inputs) with { Chapter = 13 });
        Assert.Equal("0.00", C2(result, "36").Amount);
        Assert.Contains("multiplier 0.1", C2(result, "36").Source, StringComparison.Ordinal);
        Assert.Contains("no district", Assert.Throws<MeansTestException>(() => Run(CaseFor("9000.00", inputs, district: "Outer Nowhere") with { Chapter = 13 })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_presumption_exemptions_never_apply_to_chapter_13()
    {
        var result = Run(CaseFor("4000.00", Household(under65: 2, extra: """{"disabled_veteran": true}""")) with { Chapter = 13 });
        Assert.Equal("below_median", result.Outcome);
        Assert.NotNull(result.Commitment);
    }

    [Fact]
    public void A_chapter_13_case_needs_the_household_before_either_comparison() =>
        Assert.Contains("line 16b", Assert.Throws<MeansTestException>(() => Run(CaseFor("4000.00", Inputs("{}")) with { Chapter = 13 })).Message, StringComparison.Ordinal);

    [Fact]
    public void A_chapter_with_no_means_test_form_refuses() =>
        Assert.Contains("Chapter 11 has no means-test form", Assert.Throws<MeansTestException>(() => Run(CaseFor("4000.00", Household()) with { Chapter = 11 })).Message, StringComparison.Ordinal);

    // ── beyond the Python suite: ROUND_HALF_UP at the half cent ──

    [Fact]
    public void A_sixty_month_average_on_a_half_cent_rounds_up()
    {
        // 9000.30 / 60 = 150.005 → 150.01 (half-up; banker's would give 150.00).
        var result = Run(CaseFor("9000.00", Household(under65: 1), priority: "9000.30"));
        Assert.Equal("150.01", Line(result, "35").Amount);
    }

    [Fact]
    public void The_line_36_product_on_a_half_cent_rounds_up()
    {
        // 500.05 x 0.1 = 50.005 → 50.01.
        var inputs = Household(under65: 1, extra: """{"ch13_eligible": true, "ch13_projected_plan_payment": "500.05"}""");
        Assert.Equal("50.01", Line(Run(CaseFor("9000.00", inputs)), "36").Amount);
    }

    private static MeansTestResult Run(MeansTestCase @case) => MeansTestEngine.Run(@case, Data);

    private static MeansTestInputBody Inputs(string json) => MeansTestInputRules.Parse(Build.Json(json));

    private static MeansTestInputBody Household(int under65 = 2, int over65 = 0, string? extra = null)
    {
        var body = Trees.Merge($$"""{"people_under_65": {{under65}}, "people_65_or_older": {{over65}}}""", extra);
        return MeansTestInputRules.Parse(body);
    }

    private static MeansTestInputBody FullInputs() => Household(extra: """
        {"home_secured_monthly_total": "1750.00", "vehicle_count": 1, "vehicle_1_loan_monthly": "450.00", "taxes": "1500.00",
         "involuntary_deductions": "120.00", "term_life_insurance": "40.00", "healthcare_above_allowance": "100.00", "optional_telecom": "60.00",
         "health_insurance": "400.00", "health_savings_account": "50.00", "education_under_18": "200.00", "additional_food_clothing": "50.00",
         "charitable_contributions": "25.00", "priority_cure_total": "3000.00", "ch13_eligible": true, "ch13_projected_plan_payment": "500.00"}
        """);

    private static MeansTestInputBody Ch13Inputs() => Household(extra: """
        {"home_secured_monthly_total": "1750.00", "vehicle_count": 1, "vehicle_1_loan_monthly": "450.00", "taxes": "1500.00",
         "involuntary_deductions": "120.00", "term_life_insurance": "40.00", "healthcare_above_allowance": "100.00", "optional_telecom": "60.00",
         "health_insurance": "400.00", "health_savings_account": "50.00", "education_under_18": "200.00", "additional_food_clothing": "50.00",
         "charitable_contributions": "25.00", "priority_cure_total": "3000.00", "ch13_projected_plan_payment": "500.00",
         "child_support_for_dependents": "250.00", "qualified_retirement_deductions": "150.00",
         "special_circumstances": [{"id": "sc-1", "description": "Dialysis travel", "amount": "100.00"}]}
        """);

    private static MeansTestCase FullCase() => CaseFor("7500.00", FullInputs(), priority: "6000.00", nonpriority: "30000.00", children: 1);

    private static string SecuredRow(string id, string? bucket, string monthly, string? cure = null)
    {
        var row = new JsonObject { ["id"] = id, ["creditor_name"] = $"Lender {id}", ["property_description"] = "Collateral", ["monthly_payment"] = monthly };
        if (bucket is not null)
        {
            row["bucket"] = bucket;
        }

        if (cure is not null)
        {
            row["cure_total"] = cure;
        }

        return row.ToJsonString();
    }

    private static CmiResult FlatCmi(string monthly) => new(
        Cmi.Window(AsOf),
        [],
        monthly,
        monthly == "unused" ? monthly : Py.F(Py.Dec(monthly) * 12),
        [],
        []);

    private static CmiResult PaidCmi(string debtorId, FilingRole role, decimal gross) => Cmi.CurrentMonthlyIncome(
        AsOf,
        [Build.Debtor(role, debtorId)],
        [("em-x", new EmploymentBody(debtorId, EmploymentStatus.Employed, null, "Employer", new Address(), null))],
        [.. Enumerable.Range(3, 6).Select(month => new PayPeriodRecordBody("em-x", null, null, new DateOnly(2026, month, 25), gross, null, PayFrequency.Monthly, []))],
        []);

    private static MeansTestCase CaseFor(
        string monthly,
        MeansTestInputBody inputs,
        string state = "FL",
        string county = "Alachua County",
        string district = "Middle District of Florida",
        string priority = "0.00",
        string nonpriority = "0.00",
        int children = 0,
        CmiResult? cmi = null) =>
        new(state, county, district, cmi ?? FlatCmi(monthly), inputs, priority, nonpriority, children);

    private static MeansTestLine Line(MeansTestResult result, string number) => result.Lines.First(entry => entry.Line == number);

    private static MeansTestLine C1(MeansTestResult result, string number) => result.Lines.First(entry => entry.Line == number && entry.Form == MeansTestEngine.Form122C1);

    private static MeansTestLine C2(MeansTestResult result, string number) => result.Lines.First(entry => entry.Line == number && entry.Form == MeansTestEngine.Form122C2);
}
