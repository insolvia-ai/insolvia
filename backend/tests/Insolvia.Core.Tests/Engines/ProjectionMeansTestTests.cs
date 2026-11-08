using Insolvia.Core.MeansTest;
using Insolvia.Core.Plans;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Projections.Forms;
using Insolvia.Engines.Tests.Support;
using static Insolvia.Engines.Tests.Support.Projected;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_form_projections.py — the
// means-test forms (B122A-1/A-2, B122C-1/C-2) and B113, the Chapter 13 plan,
// over the Chapter 7 and Chapter 13 reference cases.
public sealed class ProjectionMeansTestTests
{
    private static CaseFile Reference => CaseFiles.ReferenceCh7;

    private static CaseFile Reference13 => CaseFiles.ReferenceCh13;

    // --- B122A-1 / B122A-2 ---

    [Fact]
    public void B122a1_columns_are_the_cmi_derivation_verbatim()
    {
        var values = Project("form/b122a1", Reference);
        var wages = Assert.IsType<RowFills>(values["wages"]);
        Assert.Equal(Text("7,400.00"), wages["Debto1.Quest2.0"]);
        Assert.Equal(Text("0.00"), wages["Debto2.Quest2.0"]);
        Assert.Equal(Text("1,300.00"), Assert.IsType<RowFills>(values["unemployment"])["Debto2.Quest8.0"]);
        AssertRows(values["ssa_contention"], ("Debto2.Quest8A", Text("150.00")));
        Assert.Equal(Text("8,700.00"), values["total_cmi"]);
        Assert.Equal(Text("104,400.00"), values["annualized_cmi"]);
        Assert.Equal(Text("97,540.00"), values["median_income"]);
        Assert.Equal(Option("12b more than 13"), values["median_comparison"]);
        Assert.Equal(Option("Presumption of abuse applies"), values["caption.presumption_box"]);
    }

    [Fact]
    public void B122a1_lands_debtor2s_misnamed_line_4_box() =>
        Assert.Equal(new HashSet<string> { "Debto1.Quest4", "Debto2.Quest2.2" }, Assert.IsType<RowFills>(Project("form/b122a1", Reference)["household_contributions"]).Select(pair => pair.Key).ToHashSet());

    [Fact]
    public void B122a1_below_the_median_checks_box_1()
    {
        var values = Project("form/b122a1", BelowMedian(Reference));
        Assert.Equal(Option("12b less or equal to 13"), values["median_comparison"]);
        Assert.Equal(Option("No Abuse"), values["caption.presumption_box"]);
    }

    [Fact]
    public void B122a2_refuses_a_below_median_case() =>
        Assert.Contains("below the applicable median", Refused("form/b122a2", BelowMedian(Reference)).Message, StringComparison.Ordinal);

    [Fact]
    public void B122a2_determination_follows_the_engine()
    {
        var values = Project("form/b122a2", Reference);
        Assert.Equal(Text("-23,359.80"), values["disposable_income_60_months"]);
        Assert.Equal(Option("1"), values["presumption_thresholds"]);
        Assert.Equal(Option("No Abuse"), values["caption.presumption_box"]);
        Assert.False(values.ContainsKey("unsecured_ratio_determination"));
        Assert.Equal(Text("0.1"), values["ch13_multiplier"]);
        Assert.Equal(Text("65.00"), values["ch13_admin_expense"]);
        AssertRows(values["other_secured_creditor"], ("undefined_71", Text("Suncoast Equipment Finance")));
    }

    [Fact]
    public void B122a2_surfaces_the_engines_refusals_through_the_gate() =>
        Assert.Contains("household composition", Refused("form/b122a2", Reference with { MeansTestInputs = [] }).Message, StringComparison.Ordinal);

    [Fact]
    public void B122a1_takes_its_window_from_the_expected_filing_date()
    {
        var planned = Reference with { Petition = Reference.Petition! with { ExpectedFilingDate = new DateOnly(2026, 4, 15) } };
        Assert.Equal((new DateOnly(2026, 4, 15), "petition.expected_filing_date"), B122A1.MeansTestAsOf(planned));
        Assert.Equal((new DateOnly(2026, 8, 1), "case.created_at"), B122A1.MeansTestAsOf(Reference));
        Assert.Equal(Text("2,466.67"), Assert.IsType<RowFills>(Project("form/b122a1", planned)["wages"])["Debto1.Quest2.0"]);
    }

    [Fact]
    public void B122a1_line_1_takes_the_entered_marital_status_first()
    {
        var values = Project("form/b122a1", WithInputs(Reference, inputs => inputs with { MaritalFilingStatus = MaritalFilingStatus.MarriedNotFilingSeparated }));
        Assert.Equal(Option("Married but not filing"), values["marital_filing_status"]);
        Assert.Equal(Option("separately"), values["married_not_filing_household"]);
    }

    [Fact]
    public void B122a1_line_1_falls_back_to_the_debtor_records()
    {
        var (status, source) = B122A1.MaritalFilingStatus(Reference);
        Assert.Equal("married_filing_jointly", status);
        Assert.Contains("debtor_2", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("non_consumer_debts", "No Abuse")]
    [InlineData("disabled_veteran", "No Abuse")]
    [InlineData("reservist_national_guard", "Does not apply")]
    public void B122a1_an_exemption_checks_the_supplements_box(string flag, string box)
    {
        var values = Project("form/b122a1", Exempt(Reference, flag));
        Assert.Equal(Option(box), values["caption.presumption_box"]);
        Assert.False(values.ContainsKey("median_comparison"));
    }

    [Fact]
    public void B122a2_is_not_filed_by_an_exempt_debtor()
    {
        var exempt = Exempt(Reference, "reservist_national_guard");
        Assert.False(B122A2.Files(exempt));
        Assert.Contains("exempt", Refused("form/b122a2", exempt).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B122a1_prints_an_overridden_line_as_entered()
    {
        var overridden = WithInputs(Reference, inputs => inputs with { IncomeOverrides = Values(new IncomeLineOverride("ov-1", IncomeColumn.A, IncomeLineCategory.Wages, 8000.00m)) });
        var values = Project("form/b122a1", overridden);
        Assert.Equal(Text("8,000.00"), Assert.IsType<RowFills>(values["wages"])["Debto1.Quest2.0"]);
        Assert.Equal(Text("9,300.00"), values["total_cmi"]);
    }

    // --- B122C-1 / B122C-2 ---

    [Fact]
    public void B122c1_columns_are_the_cmi_derivation_under_its_own_widget_names()
    {
        var values = Project("form/b122c1", Reference13);
        var wages = Assert.IsType<RowFills>(values["wages"]);
        Assert.Equal(Text("7,400.00"), wages["undefined"]);
        Assert.Equal(Text("0.00"), wages["undefined_11"]);
        Assert.Equal(Text("1,300.00"), Assert.IsType<RowFills>(values["unemployment"])["undefined_19"]);
        AssertRows(values["ssa_contention"], ("undefined_21", Text("150.00")));
        Assert.Equal(Text("8,700.00"), values["total_cmi"]);
        Assert.Equal(["undefined_32"], FormTemplates.Latest("form/b122c1").Field("total_cmi").PdfNames);
    }

    [Fact]
    public void B122c1_above_the_median_requires_122c2_and_five_years()
    {
        var values = Project("form/b122c1", Reference13);
        Assert.Equal(Option("married"), values["marital_filing_status"]);
        Assert.Equal(Option("2"), values["marital_adjustment_status"]);
        Assert.Equal(Text("0.00"), values["marital_adjustment_total"]);
        Assert.Equal(Text("8,700.00"), values["current_monthly_income"]);
        Assert.Equal(Text("104,400.00"), values["annualized_cmi"]);
        Assert.Equal(Text("FL"), values["median_state"]);
        Assert.Equal(Text("3"), values["median_household_size"]);
        Assert.Equal(Text("97,540.00"), values["median_income"]);
        Assert.Equal(Option("17b"), values["median_comparison"]);
        Assert.Equal(Option("2"), values["caption.disposable_income_box"]);
        Assert.Equal(Text("0.00"), values["commitment_marital_adjustment"]);
        Assert.Equal(Text("8,700.00"), values["commitment_monthly_income"]);
        Assert.Equal(Text("104,400.00"), values["commitment_annualized"]);
        Assert.Equal(Option("no"), values["commitment_comparison"]);
        Assert.Equal(Option("4"), values["caption.commitment_period_box"]);
    }

    [Fact]
    public void B122c1_below_the_median_checks_17a_and_three_years()
    {
        var values = Project("form/b122c1", BelowMedian(Reference13));
        Assert.Equal(Text("69,600.00"), values["annualized_cmi"]);
        Assert.Equal(Option("17a"), values["median_comparison"]);
        Assert.Equal(Option("1"), values["caption.disposable_income_box"]);
        Assert.Equal(Option("yes"), values["commitment_comparison"]);
        Assert.Equal(Option("3"), values["caption.commitment_period_box"]);
    }

    [Fact]
    public void B122c1_leaves_lines_12_to_21_blank_until_the_household_exists()
    {
        var values = Project("form/b122c1", Reference13 with { MeansTestInputs = [] });
        Assert.Equal(Text("8,700.00"), values["total_cmi"]);
        Assert.False(values.ContainsKey("current_monthly_income"));
        Assert.False(values.ContainsKey("median_comparison"));
        Assert.False(values.ContainsKey("caption.commitment_period_box"));
    }

    [Fact]
    public void B122c1_prints_the_marital_adjustment_rows_and_the_contention()
    {
        var contended = WithInputs(Reference13, inputs => inputs with
        {
            MaritalAdjustments = Values(new MaritalAdjustmentItem("ma-1", "Spouse's own student loan", 300.00m)),
            CommitmentPeriodMaritalAdjustment = true,
        });
        var values = Project("form/b122c1", contended);
        AssertRows(values["marital_adjustment_purpose"], ("13a", Text("Spouse's own student loan")));
        Assert.Equal(Text("300.00"), values["marital_adjustment_total"]);
        Assert.Equal(Text("8,400.00"), values["current_monthly_income"]);
        Assert.Equal(Text("300.00"), values["commitment_marital_adjustment"]);
        Assert.Equal(Text("8,400.00"), values["commitment_monthly_income"]);
    }

    [Fact]
    public void B122c2_determination_follows_the_engine()
    {
        var values = Project("form/b122c2", Reference13);
        Assert.Equal(Text("3"), values["household_size"]);
        Assert.Equal(Text("9,089.33"), values["total_deductions"]);
        Assert.Equal(["undefined_85"], FormTemplates.Latest("form/b122c2").Field("total_deductions").PdfNames);
        Assert.Equal(Text("8,700.00"), values["current_monthly_income"]);
        Assert.Equal(Text("300.00"), values["child_support_income"]);
        Assert.Equal(Text("210.00"), values["qualified_retirement_deductions"]);
        AssertRows(values["special_circumstances_description"], ("43a", Text("Dialysis travel")));
        Assert.Equal(Text("180.00"), values["special_circumstances_total"]);
        Assert.Equal(Text("9,779.33"), values["total_adjustments"]);
        Assert.Equal(Text("-1,079.33"), values["monthly_disposable_income"]);
        Assert.Equal(Text("0.1"), values["ch13_multiplier"]);
        Assert.Equal(Text("650.00"), values["ch13_plan_payment"]);
        Assert.Equal(Text("65.00"), values["ch13_admin_expense"]);
        Assert.Equal(Option("12"), values["vehicle_count"]);
        Assert.Equal(Option("yes"), values["cure_claimed"]);
        Assert.Equal(Option("yes"), values["priority_claims_owed"]);
        AssertRows(values["other_secured_creditor"], ("33d", Text("Suncoast Equipment Finance")));
    }

    [Fact]
    public void B122c2_lands_13e_on_the_row_that_33c_shares()
    {
        var values = Project("form/b122c2", WithInputs(Reference13, inputs => inputs with { VehicleCount = 2, Vehicle2LoanMonthly = 220.00m }));
        Assert.Equal(Text("220.00"), values["vehicle_2_loan_total"]);
        AssertRows(values["vehicle_2_loan_rows"], ("undefined_25", Text("220.00")));
        Assert.Equal(["undefined_25", "undefined_25-1"], FormTemplates.Latest("form/b122c2").Field("vehicle_2_loan_rows").PdfNames);
    }

    [Fact]
    public void B122c2_refuses_a_below_median_case()
    {
        Assert.False(B122C2.Files(BelowMedian(Reference13)));
        Assert.Contains("at or below the applicable median", Refused("form/b122c2", BelowMedian(Reference13)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_means_test_forms_refuse_the_other_chapter()
    {
        Assert.Contains("Chapter 7 case", Refused("form/b122c1", Reference).Message, StringComparison.Ordinal);
        Assert.Contains("Chapter 7 case", Refused("form/b122c2", Reference).Message, StringComparison.Ordinal);
        Assert.False(B122C2.Files(Reference));
        Assert.Contains("Chapter 13 case", Refused("form/b122a2", Reference13).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B122c2_surfaces_the_engines_refusals_through_the_gate()
    {
        var withoutInputs = Reference13 with { MeansTestInputs = [] };
        Assert.True(B122C2.Files(withoutInputs));
        Assert.Contains("household composition", Refused("form/b122c2", withoutInputs).Message, StringComparison.Ordinal);
    }

    // --- B113, the Chapter 13 plan ---

    [Fact]
    public void B113_prints_the_calculators_figures()
    {
        var values = B113Values();
        AssertRows(values["line_2_1_amount"], ("2.1.amount.1", Text("500.00")));
        AssertRows(values["line_2_1_months"], ("2.1.months.1", Text("60")));
        Assert.Equal(Text("30,000.00"), values["line_2_5_total"]);
        Assert.Equal(new CheckFill(), values["line_1_1_included"]);
        AssertRows(values["line_3_2_rate"], ("3.2.rate.1", Text("7")));
        Assert.Equal(new CheckFill(), values["line_5_1_remaining"]);
        Assert.Equal(Text("30,000.00"), values["exhibit.total"]);
    }

    [Fact]
    public void B113_prints_a_step_and_its_lump_sums()
    {
        var values = B113Values(plan => plan with
        {
            StepPayments = Values(new StepPayment("s1", 25, 650.00m)),
            LumpSums = Values(new LumpSum("l1", 13, 1200.00m, "Tax refund")),
        });
        AssertRows(values["line_2_1_amount"], ("2.1.amount.1", Text("500.00")), ("2.1.amount.2", Text("650.00")));
        AssertRows(values["line_2_1_months"], ("2.1.months.1", Text("24")), ("2.1.months.2", Text("36")));
        Assert.Equal(new CheckFill(), values["line_2_4_additional"]);
        AssertRows(values["line_2_4_description"], ("2.4.description.1", Text("Tax refund: $1,200.00 in month 13")));
    }

    [Fact]
    public void B113_prints_a_promised_percentage()
    {
        var values = B113Values(plan => plan with { UnsecuredTreatment = UnsecuredTreatment.Percentage, UnsecuredPercentage = 45m });
        Assert.Equal(new CheckFill(), values["line_5_1_percentage"]);
        Assert.Equal(Text("45"), values["line_5_1_percentage_value"]);
        Assert.False(values.ContainsKey("line_5_1_remaining"));
    }

    [Fact]
    public void B113_lists_a_surrender_and_no_cramdown()
    {
        var values = B113Values(plan => plan with
        {
            SecuredTreatments = Values(
                new SecuredTreatment("t-home", "claim-mortgage", SecuredTreatmentKind.CureAndMaintain, null, null, null, null, null, null),
                new SecuredTreatment("t-car", "claim-auto", SecuredTreatmentKind.Surrender, null, null, null, null, null, null)),
        });
        Assert.Equal(new CheckFill(), values["line_3_5_surrender"]);
        AssertRows(values["line_3_5_creditor"], ("3.5.creditor.1", Text("Drive Away Financial LLC")));
        Assert.Equal(new CheckFill(), values["line_3_2_none"]);
        Assert.Equal(new CheckFill(), values["line_1_1_not_included"]);
    }

    [Theory]
    [InlineData("three-payment-runs", "prints 2 payment lines")]
    [InlineData("priority-paid-less-than-full", "§ 4.5")]
    [InlineData("infeasible", "not feasible")]
    [InlineData("calculator-problem", "Plan: ")]
    public void B113_refuses_a_plan_it_cannot_print_honestly(string change, string problem)
    {
        Func<PlanBody, PlanBody> edit = change switch
        {
            "three-payment-runs" => plan => plan with { StepPayments = Values(new StepPayment("s1", 13, 600.00m), new StepPayment("s2", 25, 700.00m)) },
            "priority-paid-less-than-full" => plan => plan with { PriorityPercentage = 50m },
            "infeasible" => plan => plan with { MonthlyPayment = 50.00m },
            _ => plan => plan with { TrusteePercentage = null },
        };
        var plan = edit(Reference13.Plans[0]);
        Assert.Contains(problem, Refused("form/b113", Reference13 with { Plans = [plan] }).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B113_without_a_plan_prints_the_caption_alone()
    {
        var values = Project("form/b113", Reference13 with { Plans = [] });
        Assert.Equal(Text("Ada Quinn Lovelace"), values["caption.debtor1_name"]);
        Assert.DoesNotContain(values.Keys, key => key.StartsWith("line_", StringComparison.Ordinal) || key.StartsWith("exhibit.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("7", "7")]
    [InlineData("6.250", "6.25")]
    [InlineData("10.00", "10")]
    [InlineData("100", "100")]
    public void B113_prints_a_rate_normalised(string stored, string printed) =>
        Assert.Equal(printed, B113.Rate(Exact.Dec(stored)));

    private static FieldValues B113Values(Func<PlanBody, PlanBody>? change = null) =>
        Project("form/b113", Reference13 with { Plans = [(change ?? (plan => plan))(Reference13.Plans[0])] });

    private static CaseFile BelowMedian(CaseFile caseFile) =>
        caseFile with { PayPeriodRecords = [.. caseFile.PayPeriodRecords.Select(record => record with { Gross = 4500.00m })] };

    private static CaseFile WithInputs(CaseFile caseFile, Func<MeansTestInputBody, MeansTestInputBody> change) =>
        caseFile with { MeansTestInputs = [change(caseFile.MeansTestInputs[0])] };

    private static CaseFile Exempt(CaseFile caseFile, string flag) => WithInputs(caseFile, inputs => flag switch
    {
        "non_consumer_debts" => inputs with { NonConsumerDebts = true },
        "disabled_veteran" => inputs with { DisabledVeteran = true },
        _ => inputs with { ReservistNationalGuard = true },
    });

    private static void AssertRows(FieldEntry entry, params (string Name, FieldFill Fill)[] expected)
    {
        var rows = Assert.IsType<RowFills>(entry);
        Assert.Equal(expected.Length, rows.Count);
        foreach (var (name, fill) in expected)
        {
            Assert.Equal(fill, rows[name]);
        }
    }
}
