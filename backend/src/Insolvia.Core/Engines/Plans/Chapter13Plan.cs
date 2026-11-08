using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Claims;
using Insolvia.Core.Plans;
using Insolvia.Engines.Exemptions;
using Insolvia.Engines.Liens;
using Insolvia.Engines.MeansTest;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Projections.Forms;

namespace Insolvia.Engines.Plans;

// The Chapter 13 plan calculator (from core/chapter13_plan.py, issue 16.2 /
// #366): the payment waterfall, feasibility, and the § 1325(a)(4) liquidation
// test — one pure function over the case's records and one plan proposal. It
// recomputes nothing another engine owns: the commitment period and disposable
// income are the means test's, Schedule J's net income is 106I line 12 less
// 106J line 22c, the lien figures are the lien engine's, and each asset's
// equity is the exemption workbench's.
//
// Rounding, per site: `_money` ROUND_HALF_UP; `_pct` ROUND_HALF_UP; the
// amortised payment ROUND_UP (so the last payment clears the balance); a pro
// rata share ROUND_DOWN (the remainder handed out in listing order). Powers are
// integer exponents (Exact.Pow).

/// <summary>A figure and where it came from.</summary>
public sealed record Sourced(decimal Amount, string Source);

/// <summary>What the plan reads besides the proposal: the case file and the figures other engines own.</summary>
public sealed record PlanInputs(CaseFile CaseFile)
{
    /// <summary>The applicable commitment period, in months.</summary>
    public int? CommitmentMonths { get; init; }

    /// <summary>Where the commitment period came from.</summary>
    public string? CommitmentSource { get; init; }

    /// <summary>B122C-2 line 45.</summary>
    public Sourced? DisposableIncome { get; init; }

    /// <summary>Schedule J line 23c.</summary>
    public Sourced? ScheduleJExcess { get; init; }

    /// <summary>The exemption workbench's per-asset equity.</summary>
    public IReadOnlyList<AssetExemptions> Assets { get; init; } = [];

    /// <summary>Why the figures above could not be had.</summary>
    public IReadOnlyList<string> UpstreamProblems { get; init; } = [];
}

/// <summary>One payee's line of the waterfall.</summary>
public sealed record PayoutRow(
    string Key,
    string Label,
    string? ClaimId,
    string? Treatment,
    decimal Allowed,
    decimal? MonthlyPayment,
    decimal? Rate,
    decimal Principal,
    decimal Interest,
    int? FirstMonth,
    int? LastMonth,
    decimal Unpaid,
    string Source)
{
    /// <summary>Principal plus interest.</summary>
    public decimal Payout => Principal + Interest;

    /// <summary>How many months paid anything.</summary>
    public int MonthsPaid => FirstMonth is { } first && LastMonth is { } last ? last - first + 1 : 0;
}

/// <summary>One class of the waterfall and its rows.</summary>
public sealed record ClassSummary(string Key, string Label, IReadOnlyList<PayoutRow> Rows)
{
    /// <summary>The rows' allowed amounts summed.</summary>
    public decimal Allowed => Rows.Aggregate(Chapter13Plan.Zero, (sum, row) => sum + row.Allowed);

    /// <summary>The rows' principal summed.</summary>
    public decimal Principal => Rows.Aggregate(Chapter13Plan.Zero, (sum, row) => sum + row.Principal);

    /// <summary>The rows' interest summed.</summary>
    public decimal Interest => Rows.Aggregate(Chapter13Plan.Zero, (sum, row) => sum + row.Interest);

    /// <summary>The rows' unpaid amounts summed.</summary>
    public decimal Unpaid => Rows.Aggregate(Chapter13Plan.Zero, (sum, row) => sum + row.Unpaid);

    /// <summary>The earliest month any row was paid.</summary>
    public int? FirstMonth => Rows.Where(row => row.FirstMonth is not null).Select(row => row.FirstMonth).Min();

    /// <summary>The latest month any row was paid.</summary>
    public int? LastMonth => Rows.Where(row => row.LastMonth is not null).Select(row => row.LastMonth).Max();
}

/// <summary>One claim's part of an unsecured pool, and why it is there.</summary>
public sealed record PoolClaim(string ClaimId, string Label, decimal Amount, string Source);

/// <summary>The plan's funding.</summary>
public sealed record Funding(
    int? TermMonths,
    string? TermSource,
    decimal? BasePayment,
    string? BaseSource,
    IReadOnlyList<(int Month, decimal Payment)> Schedule,
    IReadOnlyList<(int Month, decimal Amount, string? Description)> LumpSums,
    decimal Total);

/// <summary>Whether the payments cover the waterfall.</summary>
public sealed record Feasibility(bool? Feasible, decimal TotalFunding, decimal TotalDistributed, decimal Surplus, decimal Shortfall, IReadOnlyList<string> Reasons, Sourced? ScheduleJExcess, bool? ExceedsScheduleJ);

/// <summary>One asset in the liquidation test.</summary>
public sealed record LiquidationAsset(string AssetId, string? Description, decimal Value, decimal Liens, decimal Exempt, decimal Unexempt);

/// <summary>The § 1325(a)(4) liquidation test.</summary>
public sealed record Liquidation(
    IReadOnlyList<LiquidationAsset> Assets,
    decimal PropertyTotal,
    decimal LiensTotal,
    decimal ExemptionsTotal,
    decimal UnexemptTotal,
    decimal TrusteeCommission,
    string TrusteeCommissionRule,
    decimal OtherCosts,
    string OtherCostsSource,
    decimal PriorityTotal,
    decimal Available,
    IReadOnlyList<PoolClaim> Pool,
    decimal PoolTotal,
    decimal? Percentage);

/// <summary>The best-interests comparison.</summary>
public sealed record BestInterests(decimal? PlanPercentage, decimal? LiquidationPercentage, decimal PlanUnsecuredValue, decimal? PresentValueRate, bool? Passes, string Rule);

/// <summary>The plan's figures.</summary>
public sealed record PlanCalculation(
    bool PlanPresent,
    int Chapter,
    int? CommitmentMonths,
    string? CommitmentSource,
    Funding Funding,
    decimal? TrusteePercentage,
    IReadOnlyList<ClassSummary> Classes,
    IReadOnlyList<PoolClaim> UnsecuredPool,
    decimal UnsecuredPoolTotal,
    decimal? UnsecuredTarget,
    string? UnsecuredTargetSource,
    decimal? UnsecuredPercentage,
    Feasibility Feasibility,
    Liquidation Liquidation,
    BestInterests BestInterests,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Problems);

/// <summary>The Chapter 13 plan calculator (Python's <c>core/chapter13_plan.py</c>).</summary>
public static class Chapter13Plan
{
    /// <summary>The waterfall's trustee class.</summary>
    public const string ClassTrustee = "trustee";

    /// <summary>Ongoing payments through the plan.</summary>
    public const string ClassConduit = "ongoing_payments";

    /// <summary>Secured claims and arrears.</summary>
    public const string ClassSecured = "secured";

    /// <summary>Attorney's fees.</summary>
    public const string ClassAttorney = "attorney_fees";

    /// <summary>Priority claims.</summary>
    public const string ClassPriority = "priority";

    /// <summary>General unsecured claims.</summary>
    public const string ClassUnsecured = "general_unsecured";

    /// <summary>The § 326(a) schedule, cited.</summary>
    public const string Section326aRule = "11 U.S.C. § 326(a): 25% of the first $5,000, 10% of the next $45,000, 5% of the next $950,000, 3% above $1,000,000";

    internal const decimal Zero = 0.00m;

    private const decimal Hundred = 100m;
    private const decimal Twelve = 12m;

    // § 326(a): the Chapter 7 trustee's maximum compensation, as marginal rates; null is "and above".
    private static readonly (decimal? Ceiling, decimal Rate)[] Section326aTiers =
    [
        (5000m, 0.25m),
        (50000m, 0.10m),
        (1000000m, 0.05m),
        (null, 0.03m),
    ];

    private static readonly Dictionary<string, string> ClassLabels = new(StringComparer.Ordinal)
    {
        [ClassTrustee] = "Trustee's fee",
        [ClassConduit] = "Ongoing payments through the plan",
        [ClassSecured] = "Secured claims and arrears",
        [ClassAttorney] = "Attorney's fees",
        [ClassPriority] = "Priority claims",
        [ClassUnsecured] = "General unsecured claims",
    };

    /// <summary>The exemption workbench's input from a projection's case file, exemptions given positional ids.</summary>
    public static ExemptionCase ExemptionCaseOf(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        return new ExemptionCase(caseFile.Case)
        {
            Debtors = caseFile.Debtors,
            Petition = caseFile.Petition,
            Assets = caseFile.Assets,
            Claims = caseFile.Claims,
            Exemptions = [.. caseFile.Exemptions.Select((body, index) => ($"exemption-{index}", body))],
            SofaEntries = caseFile.SofaEntries,
        };
    }

    /// <summary>Compose the figures other engines own by calling them.</summary>
    public static PlanInputs BuildPlanInputs(CaseFile caseFile, ExemptionCase exemptions, DateOnly today)
    {
        var trace = MeansTestTraces.Trace(caseFile);
        var result = trace.Result;
        var commitment = result?.Commitment;
        Sourced? disposable = null;
        if (result?.DisposableIncome is { } income45)
        {
            disposable = new Sourced(Exact.Dec(income45), "the means test's monthly disposable income, B122C-2 line 45 (§ 1325(b)(2))");
        }

        var income = B106I.MonthlyIncomeLine12(caseFile);
        var expenses = B106J.MonthlyExpensesLine22c(caseFile);
        var excess = new Sourced(
            Money(income - expenses),
            $"Schedule J line 23c: 106I line 12 (${Exact.Grouped(income)}) less 106J line 22c (${Exact.Grouped(expenses)})");
        var analysis = ExemptionAnalyzer.Analyse(exemptions, today);
        return new PlanInputs(caseFile)
        {
            CommitmentMonths = commitment?.Months,
            CommitmentSource = commitment?.Source,
            DisposableIncome = disposable,
            ScheduleJExcess = excess,
            Assets = analysis.Assets,
            UpstreamProblems = [.. analysis.Problems.Select(problem => $"Exemptions (for the liquidation test): {problem}")],
        };
    }

    /// <summary>The Chapter 7 trustee's maximum commission on <paramref name="disbursed"/>.</summary>
    public static decimal Section326aCommission(decimal disbursed)
    {
        var remaining = Exact.Max(disbursed, Zero);
        var floor = 0m;
        var total = 0m;
        foreach (var (ceiling, rate) in Section326aTiers)
        {
            var band = ceiling is { } top ? Exact.Min(remaining, top - floor) : remaining;
            if (band <= 0)
            {
                break;
            }

            total += band * rate;
            remaining -= band;
            if (ceiling is { } reached)
            {
                floor = reached;
            }
        }

        return Money(total);
    }

    /// <summary>The plan's figures, pure: every refusal a problem, every doubt a warning.</summary>
    public static PlanCalculation Calculate(PlanInputs inputs, PlanBody? plan)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var problems = new List<string>(inputs.UpstreamProblems);
        var warnings = new List<string>();
        var caseFile = inputs.CaseFile;
        if (caseFile.Case.Chapter != 13)
        {
            warnings.Add($"This is a Chapter {caseFile.Case.Chapter} case; a plan is proposed only under Chapter 13.");
        }

        var liens = LienEngine.Derive(caseFile);
        var funding = FundingOf(plan, inputs, problems, warnings);
        var term = funding.TermMonths ?? 0;
        var trusteePct = plan?.TrusteePercentage;
        if (plan is not null && trusteePct is null)
        {
            problems.Add("Type the trustee's percentage fee — the district's standing trustee sets it, up to 10% (28 U.S.C. § 586(e)).");
        }

        var (conduit, emp, deficiencies) = SecuredPayees(plan, inputs, liens, Math.Max(term, 1), warnings, problems);
        var attorney = new List<Payee>();
        if (plan?.AttorneyFees is { } fees && fees > 0)
        {
            var cap = plan.AttorneyFeeMonthly;
            attorney.Add(new Payee("attorney", "Attorney's fees", null, null, Money(fees), Money(fees), 0m, null, cap is { } c ? Money(c) : null,
                "plan.attorney_fees" + (cap is { } shown && shown != 0 ? ", up to plan.attorney_fee_monthly a month" : string.Empty) + " (§ 503(b), § 507(a)(2))"));
        }

        var priority = PriorityPayees(plan, caseFile);
        var pool = PlanPool(caseFile, deficiencies);
        var poolTotal = Money(pool.Aggregate(Zero, (sum, claim) => sum + claim.Amount));
        var (target, targetSource, unsecuredKind) = UnsecuredTarget(plan, poolTotal, problems);
        var unsecuredRate = plan?.UnsecuredInterestRate;
        var unsecured = new Payee(
            "unsecured",
            "General unsecured claims, pro rata",
            null,
            unsecuredKind,
            target ?? Zero,
            target ?? Zero,
            MonthlyRate(unsecuredRate),
            unsecuredRate,
            null,
            (targetSource ?? string.Empty) + (unsecuredRate is { } ur ? $", plan.unsecured_interest_rate {Exact.F(ur)}% a year" : string.Empty));
        var trustee = new Payee(
            "trustee",
            "Trustee's fee",
            null,
            null,
            Zero,
            Zero,
            0m,
            trusteePct,
            null,
            trusteePct is { } tp ? $"plan.trustee_percentage {Exact.F(tp)}% of every receipt (28 U.S.C. § 586(e))" : "no trustee percentage typed",
            recurring: true);

        var surplus = Zero;
        var receiptsByMonth = new Dictionary<int, decimal>();
        foreach (var (month, payment) in funding.Schedule)
        {
            receiptsByMonth[month] = payment;
        }

        foreach (var (month, lump, _) in funding.LumpSums)
        {
            receiptsByMonth[month] = receiptsByMonth.GetValueOrDefault(month, Zero) + lump;
        }

        var ran = funding.Schedule.Count > 0;
        for (var month = 1; month <= funding.Schedule.Count; month++)
        {
            var receipts = receiptsByMonth.GetValueOrDefault(month, Zero);
            var fee = Money(receipts * (trusteePct ?? 0m) / Hundred);
            trustee.Pay(month, fee);
            trustee.Allowed += fee;
            var available = receipts - fee;
            foreach (var payee in emp.Concat(priority).Append(unsecured))
            {
                payee.Accrue();
            }

            foreach (var tier in new[] { conduit, emp, attorney, priority, [unsecured] })
            {
                available = Distribute(month, available, tier);
            }

            surplus += available;
        }

        var tiers = new (string Key, List<Payee> Payees)[]
        {
            (ClassTrustee, ran ? [trustee] : []),
            (ClassConduit, conduit),
            (ClassSecured, emp),
            (ClassAttorney, attorney),
            (ClassPriority, priority),
            (ClassUnsecured, [unsecured]),
        };
        var classes = tiers
            .Where(tier => tier.Payees.Count > 0)
            .Select(tier => new ClassSummary(tier.Key, ClassLabels[tier.Key], [.. tier.Payees.Select(payee => payee.Row(Math.Max(term, 1)))]))
            .ToList();
        var unsecuredRow = classes[^1].Rows[0];
        var unsecuredPaid = unsecuredRow.Payout;

        // Feasibility.
        var blocking = funding.TermMonths is null || funding.BasePayment is null;
        blocking = blocking || (plan is not null && trusteePct is null);
        blocking = blocking || target is null;
        var reasons = new List<string>();
        var shortfall = Zero;
        foreach (var summary in classes)
        {
            if (summary.Key == ClassUnsecured && unsecuredKind == "pot")
            {
                continue;
            }

            if (summary.Unpaid > 0)
            {
                shortfall += summary.Unpaid;
                reasons.Add($"{summary.Label}: ${Exact.Grouped(summary.Unpaid)} is still unpaid after month {term}.");
            }
        }

        var distributed = Money(classes.Aggregate(Zero, (sum, summary) => sum + (summary.Principal + summary.Interest)));
        var excess = inputs.ScheduleJExcess;
        decimal? peakPayment = funding.Schedule.Count > 0 ? funding.Schedule.Select(item => item.Payment).Aggregate(Exact.Max) : null;
        bool? exceeds = null;
        if (excess is not null && peakPayment is { } peak)
        {
            exceeds = peak > Money(excess.Amount);
            if (exceeds == true)
            {
                warnings.Add($"The plan payment (up to ${Exact.Grouped(peak)} a month) is more than Schedule J's monthly net income (${Exact.Grouped(excess.Amount)}, line 23c) — § 1325(a)(6) asks whether the debtor can make it.");
            }
        }

        if (unsecuredKind == "pot" && surplus > 0)
        {
            warnings.Add($"${Exact.Grouped(surplus)} is left after every class is paid in full — the payment or the term could come down.");
        }

        var feasibility = new Feasibility(blocking ? null : reasons.Count == 0, funding.Total, distributed, Money(surplus), Money(shortfall), reasons, excess, exceeds);
        var liquidation = LiquidationOf(plan, inputs, liens, warnings);

        var pvRate = plan?.PresentValueRate;
        decimal value;
        if (pvRate is { } rate)
        {
            var monthly = MonthlyRate(rate);
            value = Money(unsecured.PaidByMonth.Aggregate(Zero, (sum, pair) => sum + (pair.Value / Exact.Pow(1 + monthly, pair.Key))));
        }
        else
        {
            value = unsecuredPaid;
        }

        var planPercentage = Pct(unsecuredPaid, poolTotal);
        var testedPercentage = Pct(value, poolTotal);
        bool? passes;
        if (blocking)
        {
            passes = null;
        }
        else if (liquidation.Available <= 0)
        {
            passes = true;
        }
        else if (testedPercentage is null || liquidation.Percentage is null)
        {
            passes = null;
        }
        else
        {
            passes = testedPercentage >= liquidation.Percentage;
        }

        var bestInterests = new BestInterests(
            testedPercentage,
            liquidation.Percentage,
            value,
            pvRate,
            passes,
            "11 U.S.C. § 1325(a)(4): unsecured creditors receive at least what a Chapter 7 liquidation would pay them"
                + (pvRate is { } pv
                    ? $", valued at plan.present_value_rate {Exact.F(pv)}% a year"
                    : ", compared nominally — type a present-value rate to discount the plan's future payments"));

        if (funding.TermMonths is { } termMonths
            && inputs.CommitmentMonths is { } commitmentMonths
            && termMonths < commitmentMonths
            && planPercentage is { } pp
            && pp < Hundred)
        {
            warnings.Add($"The term ({termMonths} months) is shorter than the {commitmentMonths}-month commitment period, which § 1325(b)(4)(B) allows only when unsecured claims are paid in full.");
        }

        return new PlanCalculation(
            plan is not null,
            caseFile.Case.Chapter,
            inputs.CommitmentMonths,
            inputs.CommitmentSource,
            funding,
            trusteePct,
            classes,
            pool,
            poolTotal,
            target,
            targetSource,
            planPercentage,
            feasibility,
            liquidation,
            bestInterests,
            [.. warnings.Where(warning => warning.Length > 0)],
            problems);
    }

    /// <summary>The wire shape.</summary>
    public static JsonObject Json(PlanCalculation calc)
    {
        ArgumentNullException.ThrowIfNull(calc);
        var funding = calc.Funding;
        var feasibility = calc.Feasibility;
        var liquidation = calc.Liquidation;
        var best = calc.BestInterests;
        return new JsonObject
        {
            ["planPresent"] = calc.PlanPresent,
            ["chapter"] = calc.Chapter,
            ["commitmentPeriod"] = new JsonObject { ["months"] = calc.CommitmentMonths, ["source"] = calc.CommitmentSource },
            ["funding"] = new JsonObject
            {
                ["termMonths"] = funding.TermMonths,
                ["termSource"] = funding.TermSource,
                ["basePayment"] = M(funding.BasePayment),
                ["baseSource"] = funding.BaseSource,
                ["schedule"] = new JsonArray([.. funding.Schedule.Select(item => (JsonNode)new JsonObject { ["month"] = item.Month, ["payment"] = M(item.Payment) })]),
                ["lumpSums"] = new JsonArray([.. funding.LumpSums.Select(item => (JsonNode)new JsonObject { ["month"] = item.Month, ["amount"] = M(item.Amount), ["description"] = item.Description })]),
                ["total"] = M(funding.Total),
            },
            ["trusteePercentage"] = RateText(calc.TrusteePercentage),
            ["classes"] = new JsonArray([.. calc.Classes.Select(summary => (JsonNode)ClassJson(summary))]),
            ["unsecured"] = new JsonObject
            {
                ["pool"] = new JsonArray([.. calc.UnsecuredPool.Select(claim => (JsonNode)PoolJson(claim))]),
                ["poolTotal"] = M(calc.UnsecuredPoolTotal),
                ["target"] = M(calc.UnsecuredTarget),
                ["targetSource"] = calc.UnsecuredTargetSource,
                ["percentage"] = M(calc.UnsecuredPercentage),
            },
            ["feasibility"] = new JsonObject
            {
                ["feasible"] = feasibility.Feasible,
                ["totalFunding"] = M(feasibility.TotalFunding),
                ["totalDistributed"] = M(feasibility.TotalDistributed),
                ["surplus"] = M(feasibility.Surplus),
                ["shortfall"] = M(feasibility.Shortfall),
                ["reasons"] = new JsonArray([.. feasibility.Reasons.Select(reason => (JsonNode)reason)]),
                ["scheduleJExcess"] = feasibility.ScheduleJExcess is { } excess ? new JsonObject { ["amount"] = M(excess.Amount), ["source"] = excess.Source } : null,
                ["exceedsScheduleJ"] = feasibility.ExceedsScheduleJ,
            },
            ["liquidation"] = new JsonObject
            {
                ["assets"] = new JsonArray([.. liquidation.Assets.Select(row => (JsonNode)new JsonObject
                {
                    ["assetId"] = row.AssetId,
                    ["description"] = row.Description,
                    ["value"] = M(row.Value),
                    ["liens"] = M(row.Liens),
                    ["exempt"] = M(row.Exempt),
                    ["unexempt"] = M(row.Unexempt),
                })]),
                ["propertyTotal"] = M(liquidation.PropertyTotal),
                ["liensTotal"] = M(liquidation.LiensTotal),
                ["exemptionsTotal"] = M(liquidation.ExemptionsTotal),
                ["unexemptTotal"] = M(liquidation.UnexemptTotal),
                ["trusteeCommission"] = M(liquidation.TrusteeCommission),
                ["trusteeCommissionRule"] = liquidation.TrusteeCommissionRule,
                ["otherCosts"] = M(liquidation.OtherCosts),
                ["otherCostsSource"] = liquidation.OtherCostsSource,
                ["priorityTotal"] = M(liquidation.PriorityTotal),
                ["available"] = M(liquidation.Available),
                ["pool"] = new JsonArray([.. liquidation.Pool.Select(claim => (JsonNode)PoolJson(claim))]),
                ["poolTotal"] = M(liquidation.PoolTotal),
                ["percentage"] = M(liquidation.Percentage),
            },
            ["bestInterests"] = new JsonObject
            {
                ["planPercentage"] = M(best.PlanPercentage),
                ["liquidationPercentage"] = M(best.LiquidationPercentage),
                ["planUnsecuredValue"] = M(best.PlanUnsecuredValue),
                ["presentValueRate"] = RateText(best.PresentValueRate),
                ["passes"] = best.Passes,
                ["rule"] = best.Rule,
            },
            ["warnings"] = new JsonArray([.. calc.Warnings.Select(warning => (JsonNode)warning)]),
            ["problems"] = new JsonArray([.. calc.Problems.Select(problem => (JsonNode)problem)]),
        };
    }

    // `_money`: ROUND_HALF_UP.
    internal static decimal Money(decimal value) => Exact.Quantize(value, Rounding.HalfUp);

    private static string? M(decimal? value) => value is { } present ? Exact.F(Money(present)) : null;

    private static string? RateText(decimal? value) => value is { } present ? Exact.F(present) : null;

    private static decimal MonthlyRate(decimal? annualPercent) => annualPercent is { } rate ? rate / Hundred / Twelve : 0m;

    // `_pct`: ROUND_HALF_UP.
    private static decimal? Pct(decimal part, decimal whole) => whole <= 0 ? null : Exact.Quantize(part / whole * Hundred, Rounding.HalfUp);

    private static JsonObject RowJson(PayoutRow row) => new()
    {
        ["key"] = row.Key,
        ["label"] = row.Label,
        ["claimId"] = row.ClaimId,
        ["treatment"] = row.Treatment,
        ["allowed"] = M(row.Allowed),
        ["monthlyPayment"] = M(row.MonthlyPayment),
        ["rate"] = RateText(row.Rate),
        ["principal"] = M(row.Principal),
        ["interest"] = M(row.Interest),
        ["payout"] = M(row.Payout),
        ["firstMonth"] = row.FirstMonth,
        ["lastMonth"] = row.LastMonth,
        ["monthsPaid"] = row.MonthsPaid,
        ["unpaid"] = M(row.Unpaid),
        ["source"] = row.Source,
    };

    private static JsonObject ClassJson(ClassSummary summary) => new()
    {
        ["key"] = summary.Key,
        ["label"] = summary.Label,
        ["allowed"] = M(summary.Allowed),
        ["principal"] = M(summary.Principal),
        ["interest"] = M(summary.Interest),
        ["payout"] = M(summary.Principal + summary.Interest),
        ["unpaid"] = M(summary.Unpaid),
        ["firstMonth"] = summary.FirstMonth,
        ["lastMonth"] = summary.LastMonth,
        ["rows"] = new JsonArray([.. summary.Rows.Select(row => (JsonNode)RowJson(row))]),
    };

    private static JsonObject PoolJson(PoolClaim claim) => new()
    {
        ["claimId"] = claim.ClaimId,
        ["label"] = claim.Label,
        ["amount"] = M(claim.Amount),
        ["source"] = claim.Source,
    };

    private static string ClaimLabel(CaseFile caseFile, ClaimBody claim)
    {
        var creditor = caseFile.Creditor(claim.CreditorId);
        var name = string.IsNullOrEmpty(creditor?.Name) ? null : creditor.Name;
        var tail = string.IsNullOrEmpty(claim.AccountLast4) ? string.Empty : $" (…{claim.AccountLast4})";
        return $"{name ?? "Unnamed creditor"}{tail}";
    }

    private static List<(string Id, ClaimBody Body)> ClaimsOf(CaseFile caseFile, ClaimClass claimClass) =>
        [.. caseFile.Claims.Where(pair => pair.Body.ClaimClass == claimClass)];

    private static decimal PriorityPortion(ClaimBody claim) => claim.PriorityAmount ?? claim.Amount ?? Zero;

    private static (List<PoolClaim> Pool, List<string> Warnings) Chapter7Pool(CaseFile caseFile, LienFigures liens)
    {
        var pool = new List<PoolClaim>();
        var warnings = new List<string>();
        foreach (var (claimId, claim) in ClaimsOf(caseFile, ClaimClass.NonpriorityUnsecured))
        {
            if (claim.Amount is not { } amount)
            {
                continue;
            }

            pool.Add(new PoolClaim(claimId, ClaimLabel(caseFile, claim), Money(amount), $"claim {claimId}: nonpriority unsecured amount"));
        }

        foreach (var (claimId, claim) in ClaimsOf(caseFile, ClaimClass.PriorityUnsecured))
        {
            if (claim.NonpriorityAmount is not { } part || part == 0)
            {
                continue;
            }

            pool.Add(new PoolClaim(claimId, ClaimLabel(caseFile, claim), Money(part), $"claim {claimId}: nonpriority part of a priority claim"));
        }

        foreach (var lien in liens.Claims)
        {
            var claim = caseFile.Claims.First(pair => pair.Id == lien.ClaimId).Body;
            if (lien.UnsecuredAmount is not { } unsecured)
            {
                warnings.Add($"{ClaimLabel(caseFile, claim)}: the unsecured portion cannot be derived yet (no amount or no collateral value), so it is left out of the unsecured pool.");
                continue;
            }

            if (unsecured == 0)
            {
                continue;
            }

            pool.Add(new PoolClaim(lien.ClaimId, ClaimLabel(caseFile, claim), unsecured, $"claim {lien.ClaimId}: unsecured portion of a secured claim ({lien.UnsecuredSource})"));
        }

        return (pool, warnings);
    }

    private static (int? Term, string? Source) Term(PlanBody? plan, PlanInputs inputs, List<string> problems)
    {
        if (plan?.TermMonths is { } months)
        {
            return (months, "plan.term_months");
        }

        if (inputs.CommitmentMonths is { } commitment)
        {
            return (commitment, "the applicable commitment period, B122C-1 line 21" + (string.IsNullOrEmpty(inputs.CommitmentSource) ? string.Empty : $" — {inputs.CommitmentSource}"));
        }

        problems.Add("The plan has no term: type one, or complete the means test so the commitment period (B122C-1 line 21) can supply it.");
        return (null, null);
    }

    private static (decimal? Payment, string? Source) BasePayment(PlanBody? plan, PlanInputs inputs, List<string> problems)
    {
        if (plan is null)
        {
            problems.Add("There is no plan yet: choose a monthly payment.");
            return (null, null);
        }

        var source = plan.PaymentSource;
        if (source is null && plan.MonthlyPayment is not null)
        {
            source = PaymentSource.Fixed;
        }

        switch (source)
        {
            case PaymentSource.Fixed:
                if (plan.MonthlyPayment is not { } fixedPayment)
                {
                    problems.Add("Type the plan's monthly payment.");
                    return (null, null);
                }

                return (Money(fixedPayment), "plan.monthly_payment");
            case PaymentSource.ScheduleJExcess:
                var excess = inputs.ScheduleJExcess;
                if (excess is null || excess.Amount <= 0)
                {
                    problems.Add("Schedule J shows no monthly net income (line 23c) to fund the plan from — complete Schedules I and J, or type a payment.");
                    return (null, null);
                }

                return (Money(excess.Amount), excess.Source);
            case PaymentSource.DisposableIncome:
                var income = inputs.DisposableIncome;
                if (income is null)
                {
                    problems.Add("The means test has no § 1325(b)(2) disposable income for this case (B122C-2 is filed only above the median, and the test must be complete) — fund the plan from Schedule J or type a payment.");
                    return (null, null);
                }

                if (income.Amount <= 0)
                {
                    problems.Add("The means test's disposable income (B122C-2 line 45) is zero or less, so it cannot fund the plan.");
                    return (null, null);
                }

                return (Money(income.Amount), income.Source);
            default:
                problems.Add("Choose where the plan payment comes from: a typed amount, Schedule J's net income, or the means test's disposable income.");
                return (null, null);
        }
    }

    private static Funding FundingOf(PlanBody? plan, PlanInputs inputs, List<string> problems, List<string> warnings)
    {
        var (term, termSource) = Term(plan, inputs, problems);
        var (basePayment, baseSource) = BasePayment(plan, inputs, problems);
        var schedule = new List<(int, decimal)>();
        var lumps = new List<(int Month, decimal Amount, string? Description)>();
        var total = Zero;
        if (term is { } months && basePayment is { } payment && plan is not null)
        {
            var steps = plan.StepPayments.Where(step => step.StartMonth is not null).OrderBy(step => step.StartMonth ?? 0).ToList();
            foreach (var step in steps)
            {
                if (step.StartMonth > months)
                {
                    warnings.Add($"A step payment starts in month {step.StartMonth}, after the plan's {months}-month term, and is ignored.");
                }

                if (step.MonthlyPayment is null)
                {
                    problems.Add($"The step starting in month {step.StartMonth} has no monthly payment.");
                }
            }

            for (var month = 1; month <= months; month++)
            {
                var due = payment;
                foreach (var step in steps)
                {
                    if (step.StartMonth is { } start && start <= month && step.MonthlyPayment is { } stepPayment)
                    {
                        due = Money(stepPayment);
                    }
                }

                schedule.Add((month, due));
                total += due;
            }

            foreach (var lump in plan.LumpSums)
            {
                if (lump.Month is not { } lumpMonth || lump.Amount is not { } lumpAmount)
                {
                    continue;
                }

                if (lumpMonth > months)
                {
                    warnings.Add($"A lump sum in month {lumpMonth} falls after the plan's {months}-month term and is ignored.");
                    continue;
                }

                var value = Money(lumpAmount);
                lumps.Add((lumpMonth, value, lump.Description));
                total += value;
            }
        }

        return new Funding(term, termSource, basePayment, baseSource, schedule, [.. lumps.OrderBy(lump => lump.Month)], Money(total));
    }

    private static decimal Amortised(decimal principal, decimal monthlyRate, int months)
    {
        if (months <= 0)
        {
            return principal;
        }

        if (monthlyRate == 0)
        {
            return Exact.Quantize(principal / months, Rounding.Up);
        }

        var factor = Exact.Pow(1 + monthlyRate, months);
        var payment = principal * monthlyRate * factor / (factor - 1);
        return Exact.Quantize(payment, Rounding.Up);
    }

    private static decimal Distribute(int month, decimal available, List<Payee> payees)
    {
        var dues = payees.Select(payee => (Payee: payee, Due: payee.Due())).ToList();
        var totalDue = dues.Aggregate(Zero, (sum, item) => sum + item.Due);
        if (totalDue <= 0)
        {
            return available;
        }

        if (available >= totalDue)
        {
            foreach (var (payee, due) in dues)
            {
                payee.Pay(month, due);
            }

            return available - totalDue;
        }

        var shares = new List<(Payee Payee, decimal Share, decimal Due)>();
        var handed = Zero;
        foreach (var (payee, due) in dues)
        {
            var share = Exact.Quantize(available * due / totalDue, Rounding.Down);
            shares.Add((payee, share, due));
            handed += share;
        }

        var remainder = available - handed;
        for (var index = 0; index < shares.Count; index++)
        {
            var (payee, share, due) = shares[index];
            var extra = Exact.Min(remainder, due - share);
            if (extra > 0)
            {
                shares[index] = (payee, share + extra, due);
                remainder -= extra;
            }
        }

        foreach (var (payee, share, _) in shares)
        {
            payee.Pay(month, share);
        }

        return remainder;
    }

    private static (List<Payee> Conduit, List<Payee> Emp, List<PoolClaim> Deficiencies) SecuredPayees(PlanBody? plan, PlanInputs inputs, LienFigures liens, int term, List<string> warnings, List<string> problems)
    {
        var caseFile = inputs.CaseFile;
        var conduit = new List<Payee>();
        var emp = new List<Payee>();
        var deficiencies = new List<PoolClaim>();
        var securedIds = ClaimsOf(caseFile, ClaimClass.Secured).Select(pair => pair.Id).ToHashSet(StringComparer.Ordinal);

        if (plan is not null)
        {
            for (var index = 0; index < plan.SecuredTreatments.Count; index++)
            {
                var row = plan.SecuredTreatments[index];
                if (row.ClaimId is not null && !securedIds.Contains(row.ClaimId))
                {
                    problems.Add($"Treatment {index + 1} names a claim that is not a secured claim of this case — fix the reference or remove the row.");
                }
            }
        }

        foreach (var (claimId, claim) in ClaimsOf(caseFile, ClaimClass.Secured))
        {
            var label = ClaimLabel(caseFile, claim);
            var lien = liens.Claim(claimId);
            var treatment = plan?.SecuredTreatments.FirstOrDefault(t => t.ClaimId == claimId);
            var path = treatment is not null ? $"plan.secured_treatments[{treatment.Id}]" : string.Empty;
            if (treatment?.Treatment is not { } kind)
            {
                if (plan is null)
                {
                    continue;
                }

                warnings.Add($"{label}: no treatment chosen, so the plan pays this secured claim nothing — § 1325(a)(5) requires one unless the holder accepts the plan.");
                continue;
            }

            if (kind == SecuredTreatmentKind.Surrender)
            {
                if (lien?.UnsecuredAmount is { } deficiency && deficiency != 0)
                {
                    deficiencies.Add(new PoolClaim(claimId, label, deficiency, $"claim {claimId}: deficiency after surrender (§ 1325(a)(5)(C)), the claim less the collateral's value"));
                }

                continue;
            }

            var rate = kind == SecuredTreatmentKind.CureAndMaintain ? treatment.ArrearageInterestRate : treatment.InterestRate;
            var fixedPayment = treatment.MonthlyPayment;
            decimal principal;
            string source;
            string payeeLabel;
            if (kind == SecuredTreatmentKind.CureAndMaintain)
            {
                if (treatment.MaintenancePayment is { } maintenance)
                {
                    conduit.Add(new Payee(
                        $"conduit:{claimId}",
                        $"{label} — ongoing payment",
                        claimId,
                        kind.ToWire(),
                        Money(maintenance * term),
                        Zero,
                        0m,
                        null,
                        Money(maintenance),
                        $"{path}.maintenance_payment, every month of the term (§ 1322(b)(5))",
                        recurring: true));
                }

                if (treatment.Arrearage is not { } arrearage || arrearage == 0)
                {
                    continue;
                }

                principal = arrearage;
                source = $"{path}.arrearage, cured under § 1322(b)(5)";
                payeeLabel = $"{label} — arrearage";
            }
            else
            {
                var claimAmount = claim.Amount;
                if (treatment.CramdownValue is { } typed)
                {
                    principal = claimAmount is { } owed ? Exact.Min(typed, owed) : typed;
                    source = $"{path}.cramdown_value (§ 506(a), § 1325(a)(5)(B))";
                    if (claimAmount is { } whole && whole > principal)
                    {
                        deficiencies.Add(new PoolClaim(claimId, label, Money(whole - principal), $"claim {claimId}: the claim less {path}.cramdown_value"));
                    }
                }
                else if (lien?.SecuredAmount is { } securedAmount)
                {
                    principal = securedAmount;
                    source = $"claim {claimId}: secured portion, the collateral's value less senior liens (§ 506(a), § 1325(a)(5)(B))";
                    if (lien.UnsecuredAmount is { } bifurcated && bifurcated != 0)
                    {
                        deficiencies.Add(new PoolClaim(claimId, label, bifurcated, $"claim {claimId}: unsecured portion, bifurcated under § 506(a)"));
                    }
                }
                else
                {
                    problems.Add($"{label}: a cramdown needs a value — type one, or give the claim an amount and its collateral a value.");
                    continue;
                }

                if (rate is null)
                {
                    problems.Add($"{label}: a cramdown needs an interest rate (§ 1325(a)(5)(B)(ii), Till v. SCS Credit Corp.).");
                }

                payeeLabel = $"{label} — secured value";
            }

            var monthlyRate = MonthlyRate(rate);
            var payment = fixedPayment ?? Amortised(principal, monthlyRate, term);
            if (rate is { } annual)
            {
                source += $", {Exact.F(annual)}% a year";
            }

            source += fixedPayment is not null ? $"; {path}.monthly_payment" : $"; amortised over {term} months";
            emp.Add(new Payee($"secured:{claimId}", payeeLabel, claimId, kind.ToWire(), Money(principal), Money(principal), monthlyRate, rate, payment, source));
        }

        return (conduit, emp, deficiencies);
    }

    private static List<Payee> PriorityPayees(PlanBody? plan, CaseFile caseFile)
    {
        var share = plan?.PriorityPercentage;
        var rate = plan?.PriorityInterestRate;
        var payees = new List<Payee>();
        foreach (var (claimId, claim) in ClaimsOf(caseFile, ClaimClass.PriorityUnsecured))
        {
            var portion = PriorityPortion(claim);
            if (portion <= 0)
            {
                continue;
            }

            var owed = Money(portion * (share ?? Hundred) / Hundred);
            var source = $"claim {claimId}: priority amount"
                + (share is { } s ? $" at plan.priority_percentage {Exact.F(s)}%" : ", paid in full (§ 1322(a)(2))");
            if (rate is { } r)
            {
                source += $", plan.priority_interest_rate {Exact.F(r)}% a year";
            }

            payees.Add(new Payee($"priority:{claimId}", ClaimLabel(caseFile, claim), claimId, null, owed, owed, MonthlyRate(rate), rate, null, source));
        }

        return payees;
    }

    private static List<PoolClaim> PlanPool(CaseFile caseFile, List<PoolClaim> deficiencies)
    {
        var pool = new List<PoolClaim>();
        foreach (var (claimId, claim) in ClaimsOf(caseFile, ClaimClass.NonpriorityUnsecured))
        {
            if (claim.Amount is not { } amount)
            {
                continue;
            }

            pool.Add(new PoolClaim(claimId, ClaimLabel(caseFile, claim), Money(amount), $"claim {claimId}: nonpriority unsecured amount"));
        }

        foreach (var (claimId, claim) in ClaimsOf(caseFile, ClaimClass.PriorityUnsecured))
        {
            if (claim.NonpriorityAmount is not { } part || part == 0)
            {
                continue;
            }

            pool.Add(new PoolClaim(claimId, ClaimLabel(caseFile, claim), Money(part), $"claim {claimId}: nonpriority part of a priority claim"));
        }

        return [.. pool, .. deficiencies];
    }

    private static (decimal? Target, string? Source, string Treatment) UnsecuredTarget(PlanBody? plan, decimal poolTotal, List<string> problems)
    {
        var treatment = plan?.UnsecuredTreatment?.ToWire() ?? "pot";
        if (treatment == "percentage")
        {
            if (plan?.UnsecuredPercentage is not { } share)
            {
                problems.Add("Type the percentage the plan promises unsecured creditors.");
                return (null, null, treatment);
            }

            return (Money(poolTotal * share / Hundred), $"plan.unsecured_percentage {Exact.F(share)}% of the unsecured pool", treatment);
        }

        if (treatment == "amount")
        {
            if (plan?.UnsecuredAmount is not { } promised)
            {
                problems.Add("Type the amount the plan promises unsecured creditors.");
                return (null, null, treatment);
            }

            return (Money(promised), "plan.unsecured_amount", treatment);
        }

        return (poolTotal, "whatever remains, up to the full unsecured pool (a pot plan)", treatment);
    }

    private static Liquidation LiquidationOf(PlanBody? plan, PlanInputs inputs, LienFigures liens, List<string> warnings)
    {
        var caseFile = inputs.CaseFile;
        var rows = new List<LiquidationAsset>();
        foreach (var asset in inputs.Assets)
        {
            if (asset.CurrentValue is null)
            {
                warnings.Add($"{(string.IsNullOrEmpty(asset.Description) ? "An asset" : asset.Description)} has no value on Schedule A/B, so the liquidation test counts it at $0.");
            }

            var value = asset.CurrentValue is { } current && current != 0 ? current : Zero;
            var equity = asset.NetEquity ?? Zero;
            var unexempt = asset.Unexempt ?? Zero;
            rows.Add(new LiquidationAsset(asset.AssetId, asset.Description, Money(value), Money(value - equity), Money(equity - unexempt), Money(unexempt)));
        }

        var unexemptTotal = rows.Aggregate(Zero, (sum, row) => sum + row.Unexempt);
        var commission = Section326aCommission(unexemptTotal);
        var other = plan is not null ? Money(plan.Chapter7OtherCosts is { } costs && costs != 0 ? costs : Zero) : Zero;
        var priorityTotal = Money(ClaimsOf(caseFile, ClaimClass.PriorityUnsecured).Aggregate(Zero, (sum, pair) => sum + PriorityPortion(pair.Body)));
        var available = Money(Exact.Max(unexemptTotal - commission - other - priorityTotal, Zero));
        var (pool, poolWarnings) = Chapter7Pool(caseFile, liens);
        warnings.AddRange(poolWarnings);
        var poolTotal = Money(pool.Aggregate(Zero, (sum, claim) => sum + claim.Amount));
        var percentage = Pct(available, poolTotal);
        if (percentage is { } pct && pct > Hundred)
        {
            percentage = Hundred;
        }

        var otherSource = plan?.Chapter7OtherCosts is not null
            ? "plan.chapter_7_other_costs" + (string.IsNullOrEmpty(plan.Chapter7OtherCostsDescription) ? string.Empty : $" ({plan.Chapter7OtherCostsDescription})")
            : "none typed";
        return new Liquidation(
            rows,
            Money(rows.Aggregate(Zero, (sum, row) => sum + row.Value)),
            Money(rows.Aggregate(Zero, (sum, row) => sum + row.Liens)),
            Money(rows.Aggregate(Zero, (sum, row) => sum + row.Exempt)),
            Money(unexemptTotal),
            commission,
            Section326aRule,
            other,
            otherSource,
            priorityTotal,
            available,
            pool,
            poolTotal,
            percentage);
    }

    /// <summary>A payee's running state during the simulation.</summary>
    private sealed class Payee(string key, string label, string? claimId, string? treatment, decimal allowed, decimal balance, decimal monthlyRate, decimal? rate, decimal? monthlyPayment, string source, bool recurring = false)
    {
        public decimal Allowed { get; set; } = allowed;

        public Dictionary<int, decimal> PaidByMonth { get; } = [];

        private decimal Balance { get; set; } = balance;

        private decimal Principal { get; set; } = Zero;

        private decimal Interest { get; set; } = Zero;

        private int? FirstMonth { get; set; }

        private int? LastMonth { get; set; }

        private decimal AccruedUnpaid { get; set; } = Zero;

        public void Accrue()
        {
            if (recurring || Balance <= 0 || monthlyRate == 0)
            {
                return;
            }

            var accrued = Money(Balance * monthlyRate);
            Balance += accrued;
            AccruedUnpaid += accrued;
        }

        public decimal Due()
        {
            if (recurring)
            {
                return monthlyPayment is { } recurringPayment && recurringPayment != 0 ? recurringPayment : Zero;
            }

            if (Balance <= 0)
            {
                return Zero;
            }

            return monthlyPayment is { } payment ? Exact.Min(payment, Balance) : Balance;
        }

        public void Pay(int month, decimal amount)
        {
            if (amount <= 0)
            {
                return;
            }

            if (!recurring)
            {
                Balance -= amount;
                var toInterest = Exact.Min(amount, AccruedUnpaid);
                AccruedUnpaid -= toInterest;
                Interest += toInterest;
                Principal += amount - toInterest;
            }
            else
            {
                Principal += amount;
            }

            FirstMonth ??= month;
            LastMonth = month;
            PaidByMonth[month] = PaidByMonth.GetValueOrDefault(month, Zero) + amount;
        }

        public PayoutRow Row(int term)
        {
            decimal unpaid;
            if (recurring)
            {
                var owed = (monthlyPayment is { } payment && payment != 0 ? payment : Zero) * term;
                unpaid = Money(Exact.Max(owed - Principal, Zero));
            }
            else
            {
                unpaid = Money(Exact.Max(Balance, Zero));
            }

            return new PayoutRow(key, label, claimId, treatment, Money(Allowed), monthlyPayment, rate, Money(Principal), Money(Interest), FirstMonth, LastMonth, unpaid, source);
        }
    }
}
