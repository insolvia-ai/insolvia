using Insolvia.Domain.Assets;
using Insolvia.Domain.Claims;
using Insolvia.Domain.Creditors;
using Insolvia.Domain.Engines.Exemptions;
using Insolvia.Domain.Engines.Plans;
using Insolvia.Domain.Engines.Projections;
using Insolvia.Domain.Engines.Tests.Support;
using Insolvia.Domain.Plans;
using static Insolvia.Domain.Engines.Plans.Chapter13Plan;

namespace Insolvia.Domain.Engines.Tests;

// Translated from services/api/tests/unit/test_chapter13_plan.py: the plan
// calculator over a hand-built case file and hand-typed upstream figures.
// The reference plan, by hand: 60 months at $600 = $36,000 in; trustee 10%
// $3,600; auto cramdown to $9,000 at 0% $9,000; mortgage arrearage $6,000;
// attorney $3,000; priority $3,000; unsecured what remains, $11,400 of a
// $24,000 pool (47.50%). Liquidation: $8,000 unexempt less the § 326(a)
// commission ($1,550) less $3,000 priority = $3,450 of $24,000 (14.38%).
public sealed class Chapter13PlanTests
{
    private const string ReferencePlan = """
        {"termMonths": 60, "paymentSource": "fixed", "monthlyPayment": 600, "trusteePercentage": 10, "attorneyFees": 3000,
         "securedTreatments": [
           {"id": "t-auto", "claimId": "claim-auto", "treatment": "cramdown", "cramdownValue": 9000, "interestRate": 0},
           {"id": "t-home", "claimId": "claim-mortgage", "treatment": "cure_and_maintain", "arrearage": 6000}],
         "unsecuredTreatment": "pot"}
        """;

    private static readonly CaseFile File = new(Build.Case(chapter: 13))
    {
        Assets =
        [
            ("asset-house", Build.Body(AssetRules.Kind, """{"description": "12 Example Court", "valueEntire": 250000}""")),
            ("asset-car", Build.Body(AssetRules.Kind, """{"description": "2019 sedan", "valueEntire": 9000}""")),
            ("asset-savings", Build.Body(AssetRules.Kind, """{"description": "Savings", "valueEntire": 10000}""")),
        ],
        Creditors =
        [
            ("cr-mortgage", Build.Body(CreditorRules.Kind, """{"name": "Example Mortgage Co"}""")),
            ("cr-auto", Build.Body(CreditorRules.Kind, """{"name": "Example Auto Finance"}""")),
            ("cr-tax", Build.Body(CreditorRules.Kind, """{"name": "Example Tax Authority"}""")),
            ("cr-card", Build.Body(CreditorRules.Kind, """{"name": "Example Card Bank"}""")),
        ],
        Claims =
        [
            ("claim-mortgage", Build.Body(ClaimRules.Kind, """{"creditorId": "cr-mortgage", "claimClass": "secured", "amount": 200000.00, "assetId": "asset-house", "lienPosition": 1}""")),
            ("claim-auto", Build.Body(ClaimRules.Kind, """{"creditorId": "cr-auto", "claimClass": "secured", "amount": 12000.00, "assetId": "asset-car", "lienPosition": 1}""")),
            ("claim-tax", Build.Body(ClaimRules.Kind, """{"creditorId": "cr-tax", "claimClass": "priority_unsecured", "priorityAmount": 3000.00, "nonpriorityAmount": 1000.00}""")),
            ("claim-card", Build.Body(ClaimRules.Kind, """{"creditorId": "cr-card", "claimClass": "nonpriority_unsecured", "amount": 20000.00}""")),
        ],
    };

    private static readonly PlanInputs Inputs = new(File)
    {
        CommitmentMonths = 60,
        CommitmentSource = "line 20b is more than or equal to line 20c",
        ScheduleJExcess = new Sourced(700.00m, "Schedule J line 23c"),
        Assets = [Equity("asset-house", 250000m, 200000m, 50000m), Equity("asset-car", 9000m, 12000m, 0m), Equity("asset-savings", 10000m, 0m, 2000m)],
    };

    // ── the reference plan ──

    [Fact]
    public void The_reference_plan_funds_every_class_in_order()
    {
        var calc = Run();
        Assert.Equal(36000.00m, calc.Funding.Total);
        Assert.Equal(3600.00m, ByClass(calc, ClassTrustee).Principal);
        Assert.Equal(15000.00m, ByClass(calc, ClassSecured).Principal);
        Assert.Equal(3000.00m, ByClass(calc, ClassAttorney).Principal);
        Assert.Equal(3000.00m, ByClass(calc, ClassPriority).Principal);
        Assert.Equal(11400.00m, ByClass(calc, ClassUnsecured).Principal);
        Assert.Equal(0.00m, calc.Feasibility.Surplus);
    }

    [Fact]
    public void The_reference_plan_is_feasible_and_clears_the_liquidation_floor()
    {
        var calc = Run();
        Assert.True(calc.Feasibility.Feasible);
        Assert.Equal(24000.00m, calc.UnsecuredPoolTotal);
        Assert.Equal(47.50m, calc.UnsecuredPercentage);
        Assert.Equal(8000.00m, calc.Liquidation.UnexemptTotal);
        Assert.Equal(1550.00m, calc.Liquidation.TrusteeCommission);
        Assert.Equal(3450.00m, calc.Liquidation.Available);
        Assert.Equal(14.38m, calc.Liquidation.Percentage);
        Assert.True(calc.BestInterests.Passes);
    }

    [Fact]
    public void Every_row_names_its_claim_and_its_source()
    {
        var calc = Run();
        var auto = ByClass(calc, ClassSecured).Rows.Single(row => row.ClaimId == "claim-auto");
        Assert.Contains("plan.secured_treatments[t-auto].cramdown_value", auto.Source, StringComparison.Ordinal);
        Assert.StartsWith("Example Auto Finance", auto.Label, StringComparison.Ordinal);
        var poolSources = calc.UnsecuredPool.ToDictionary(claim => claim.ClaimId, claim => claim.Source);
        Assert.Contains("cramdown_value", poolSources["claim-auto"], StringComparison.Ordinal);
        Assert.Contains("nonpriority part", poolSources["claim-tax"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_attorney_is_paid_before_priority_claims()
    {
        var calc = Run();
        Assert.Equal((1, 11), (ByClass(calc, ClassAttorney).FirstMonth!.Value, ByClass(calc, ClassAttorney).LastMonth!.Value));
        Assert.Equal(11, ByClass(calc, ClassPriority).FirstMonth);
    }

    [Fact]
    public void The_json_carries_money_as_strings()
    {
        var body = Json(Run());
        Assert.Equal("47.50", body["unsecured"]!["percentage"]!.GetValue<string>());
        Assert.True(body["feasibility"]!["feasible"]!.GetValue<bool>());
        Assert.True(body["bestInterests"]!["passes"]!.GetValue<bool>());
        Assert.Equal("1550.00", body["liquidation"]!["trusteeCommission"]!.GetValue<string>());
        Assert.Equal([ClassTrustee, ClassSecured, ClassAttorney, ClassPriority, ClassUnsecured], body["classes"]!.AsArray().Select(c => c!["key"]!.GetValue<string>()));
    }

    // ── feasibility ──

    [Fact]
    public void A_payment_too_small_for_the_waterfall_is_not_feasible()
    {
        var calc = Run("""{"monthlyPayment": 280}""");
        Assert.False(calc.Feasibility.Feasible);
        Assert.True(calc.Feasibility.Shortfall > 0);
        Assert.Contains(calc.Feasibility.Reasons, reason => reason.Contains("unpaid after month 60", StringComparison.Ordinal));
    }

    [Fact]
    public void A_promised_percentage_the_payment_cannot_meet_is_not_feasible()
    {
        var calc = Run("""{"unsecuredTreatment": "percentage", "unsecuredPercentage": 60}""");
        Assert.Equal(14400.00m, calc.UnsecuredTarget);
        Assert.False(calc.Feasibility.Feasible);
    }

    [Fact]
    public void A_promised_amount_the_payment_covers_is_feasible_with_a_surplus()
    {
        var calc = Run("""{"unsecuredTreatment": "amount", "unsecuredAmount": 10000}""");
        Assert.True(calc.Feasibility.Feasible);
        Assert.Equal(1400.00m, calc.Feasibility.Surplus);
    }

    [Fact]
    public void A_payment_above_schedule_j_is_flagged()
    {
        var calc = Run("""{"monthlyPayment": 800}""");
        Assert.True(calc.Feasibility.ExceedsScheduleJ);
        Assert.Contains(calc.Warnings, warning => warning.Contains("§ 1325(a)(6)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_trustee_percentage_leaves_feasibility_undetermined()
    {
        var calc = Calculate(Inputs, Plan(remove: "trusteePercentage"));
        Assert.Null(calc.Feasibility.Feasible);
        Assert.Null(calc.BestInterests.Passes);
        Assert.Contains(calc.Problems, problem => problem.Contains("trustee's percentage", StringComparison.Ordinal));
    }

    // ── funding ──

    [Fact]
    public void The_term_defaults_to_the_commitment_period()
    {
        var calc = Calculate(Inputs with { CommitmentMonths = 36 }, Plan(remove: "termMonths"));
        Assert.Equal(36, calc.Funding.TermMonths);
        Assert.Contains("B122C-1 line 21", calc.Funding.TermSource ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_term_or_a_means_test_there_is_no_waterfall()
    {
        var calc = Calculate(Inputs with { CommitmentMonths = null }, Plan(remove: "termMonths"));
        Assert.Empty(calc.Funding.Schedule);
        Assert.Null(calc.Feasibility.Feasible);
        Assert.Contains(calc.Problems, problem => problem.Contains("no term", StringComparison.Ordinal));
    }

    [Fact]
    public void Steps_and_lump_sums_change_the_funding()
    {
        var calc = Run("""{"stepPayments": [{"id": "s1", "startMonth": 31, "monthlyPayment": 700}], "lumpSums": [{"id": "l1", "month": 12, "amount": 1500}]}""");
        var schedule = calc.Funding.Schedule.ToDictionary(item => item.Month, item => item.Payment);
        Assert.Equal((600.00m, 700.00m, 700.00m), (schedule[30], schedule[31], schedule[60]));
        Assert.Equal((600m * 30) + (700m * 30) + 1500, calc.Funding.Total);
    }

    [Fact]
    public void The_schedule_j_source_uses_line_23c()
    {
        var calc = Run("""{"paymentSource": "schedule_j_excess", "monthlyPayment": null}""");
        Assert.Equal(700.00m, calc.Funding.BasePayment);
        Assert.Equal("Schedule J line 23c", calc.Funding.BaseSource);
    }

    [Fact]
    public void The_disposable_income_source_needs_the_means_test()
    {
        var calc = Run("""{"paymentSource": "disposable_income", "monthlyPayment": null}""");
        Assert.Null(calc.Funding.BasePayment);
        Assert.Contains(calc.Problems, problem => problem.Contains("B122C-2", StringComparison.Ordinal));
    }

    [Fact]
    public void The_disposable_income_source_is_the_engines_figure()
    {
        var calc = Run("""{"paymentSource": "disposable_income", "monthlyPayment": null}""", Inputs with { DisposableIncome = new Sourced(612.40m, "B122C-2 line 45") });
        Assert.Equal(612.40m, calc.Funding.BasePayment);
        Assert.Equal("B122C-2 line 45", calc.Funding.BaseSource);
    }

    // ── treatments ──

    [Fact]
    public void A_cramdown_at_a_rate_pays_the_value_plus_interest()
    {
        var calc = Run("""{"monthlyPayment": 900, "securedTreatments": [{"id": "t-auto", "claimId": "claim-auto", "treatment": "cramdown", "cramdownValue": 9000, "interestRate": 6}]}""");
        var auto = ByClass(calc, ClassSecured).Rows[0];
        Assert.Equal(174.00m, auto.MonthlyPayment);
        Assert.Equal(9000.00m, auto.Principal);
        Assert.InRange(auto.Interest, 1430.01m, 1439.99m);
        Assert.Equal(0.00m, auto.Unpaid);
    }

    [Fact]
    public void A_cramdown_without_a_value_uses_the_derived_secured_portion()
    {
        var calc = Run("""{"securedTreatments": [{"id": "t-auto", "claimId": "claim-auto", "treatment": "cramdown", "interestRate": 0}]}""");
        Assert.Equal(9000.00m, ByClass(calc, ClassSecured).Rows[0].Allowed);
        Assert.Contains("bifurcated", calc.UnsecuredPool.First(c => c.ClaimId == "claim-auto").Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_surrendered_claims_deficiency_joins_the_unsecured_pool()
    {
        var calc = Run("""{"securedTreatments": [{"id": "t-auto", "claimId": "claim-auto", "treatment": "surrender"}]}""");
        Assert.DoesNotContain(calc.Classes.SelectMany(s => s.Rows), row => row.ClaimId == "claim-auto");
        Assert.Contains(calc.UnsecuredPool, c => c.ClaimId == "claim-auto" && c.Amount == 3000.00m);
    }

    [Fact]
    public void A_conduit_payment_is_paid_every_month_of_the_term()
    {
        var calc = Run("""{"monthlyPayment": 2200, "securedTreatments": [{"id": "t-home", "claimId": "claim-mortgage", "treatment": "cure_and_maintain", "arrearage": 6000, "maintenancePayment": 1500}]}""");
        var conduit = ByClass(calc, ClassConduit).Rows[0];
        Assert.Equal(90000.00m, conduit.Principal);
        Assert.Equal((1, 60), (conduit.FirstMonth!.Value, conduit.LastMonth!.Value));
    }

    [Fact]
    public void An_untreated_secured_claim_is_warned_about_not_paid()
    {
        var calc = Run("""{"securedTreatments": []}""");
        Assert.Contains(calc.Warnings, w => w.Contains("Example Auto Finance", StringComparison.Ordinal) && w.Contains("§ 1325(a)(5)", StringComparison.Ordinal));
        Assert.DoesNotContain(ClassSecured, calc.Classes.Select(s => s.Key));
    }

    [Fact]
    public void A_treatment_naming_a_non_secured_claim_is_a_problem() =>
        Assert.Contains(Run("""{"securedTreatments": [{"id": "t-x", "claimId": "claim-card", "treatment": "surrender"}]}""").Problems, p => p.Contains("not a secured claim", StringComparison.Ordinal));

    [Fact]
    public void A_reduced_priority_percentage_pays_less() =>
        Assert.Equal(1500.00m, ByClass(Run("""{"priorityPercentage": 50}"""), ClassPriority).Principal);

    // ── the liquidation test ──

    [Theory]
    [InlineData("0", "0.00")]
    [InlineData("4000", "1000.00")]
    [InlineData("5000", "1250.00")]
    [InlineData("50000", "5750.00")]
    [InlineData("1000000", "53250.00")]
    [InlineData("1100000", "56250.00")]
    public void The_section_326a_commission_follows_the_statutes_bands(string disbursed, string commission) =>
        Assert.Equal(Exact.Dec(commission), Section326aCommission(Exact.Dec(disbursed)));

    [Fact]
    public void Other_chapter_7_costs_lower_the_floor() =>
        Assert.Equal(2450.00m, Run("""{"chapter7OtherCosts": 1000}""").Liquidation.Available);

    [Fact]
    public void A_plan_below_the_floor_fails_the_best_interests_test()
    {
        var calc = Run(null, Inputs with { Assets = [Inputs.Assets[0], Inputs.Assets[1], Equity("asset-savings", 40000m, 0m, 2000m)] });
        Assert.Equal(100m, calc.Liquidation.Percentage);
        Assert.False(calc.BestInterests.Passes);
    }

    [Fact]
    public void A_present_value_rate_discounts_the_plans_unsecured_payments()
    {
        var nominal = Run();
        var discounted = Run("""{"presentValueRate": 5}""");
        Assert.True(discounted.BestInterests.PlanPercentage < nominal.BestInterests.PlanPercentage);
        Assert.Contains("present_value_rate", discounted.BestInterests.Rule, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_plan_the_liquidation_floor_is_still_reported()
    {
        var calc = Calculate(Inputs, null);
        Assert.False(calc.PlanPresent);
        Assert.Equal(14.38m, calc.Liquidation.Percentage);
        Assert.Null(calc.Feasibility.Feasible);
        Assert.Contains(calc.Problems, p => p.Contains("no plan yet", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chapter_7_case_is_warned() =>
        Assert.Contains(Run(null, Inputs with { CaseFile = File with { Case = File.Case with { Chapter = 7 } } }).Warnings, w => w.Contains("Chapter 7 case", StringComparison.Ordinal));

    // ── beyond the Python suite: the rounding modes the waterfall uses ──

    [Fact]
    public void The_amortised_payment_rounds_up_and_a_pro_rata_share_rounds_down()
    {
        // $1,000 over 3 months at 0% is 333.333… — ROUND_UP gives 333.34, so
        // the last payment clears the balance.
        var calc = Run("""{"termMonths": 3, "monthlyPayment": 5000, "trusteePercentage": 0, "attorneyFees": 0, "securedTreatments": [{"id": "t-auto", "claimId": "claim-auto", "treatment": "cramdown", "cramdownValue": 1000, "interestRate": 0}]}""");
        var auto = ByClass(calc, ClassSecured).Rows[0];
        Assert.Equal(333.34m, auto.MonthlyPayment);
        Assert.Equal(1000.00m, auto.Principal);
        Assert.Equal(0.00m, auto.Unpaid);
    }

    private static AssetExemptions Equity(string assetId, decimal value, decimal liens, decimal exempt)
    {
        var net = Exact.Max(value - liens, 0m);
        return new AssetExemptions(assetId, assetId, null, value, liens, net, exempt, Exact.Max(net - exempt, 0m), null, false, [], []);
    }

    private static PlanBody Plan(string? overrides = null, string? remove = null)
    {
        var body = Trees.Merge(ReferencePlan, overrides);
        if (remove is not null)
        {
            body.Remove(remove);
        }

        return CaseFiles.Parse(PlanRules.Kind, body);
    }

    private static PlanCalculation Run(string? overrides = null, PlanInputs? inputs = null) => Calculate(inputs ?? Inputs, Plan(overrides));

    private static ClassSummary ByClass(PlanCalculation calc, string key) => calc.Classes.First(summary => summary.Key == key);
}
