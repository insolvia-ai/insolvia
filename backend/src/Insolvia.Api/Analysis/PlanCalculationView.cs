using Insolvia.Domain.Engines;
using Insolvia.Domain.Engines.Plans;

namespace Insolvia.Api.Analysis;

/// <summary>
/// A Chapter 13 plan's figures: its funding, the payout by class, the
/// unsecured pool, feasibility, the Chapter 7 liquidation floor and the best
/// interests test. Money is a decimal string to the cent (rounded half up);
/// a figure that cannot be derived yet is null.
/// </summary>
/// <param name="PlanPresent">Whether a plan was calculated (false: the liquidation floor alone).</param>
/// <param name="Chapter">The case's chapter.</param>
/// <param name="CommitmentPeriod">The applicable commitment period.</param>
/// <param name="Funding">What the plan pays in.</param>
/// <param name="TrusteePercentage">The trustee's percentage fee; null when unknown.</param>
/// <param name="Classes">The payout, class by class, in distribution order.</param>
/// <param name="Unsecured">The general unsecured pool and what it receives.</param>
/// <param name="Feasibility">Whether the funding covers the payout.</param>
/// <param name="Liquidation">What unsecured creditors would receive in Chapter 7.</param>
/// <param name="BestInterests">§ 1325(a)(4): the plan against liquidation.</param>
/// <param name="Warnings">What a preparer should check, for a person.</param>
/// <param name="Problems">What is missing, for a person.</param>
public sealed record PlanCalculationView(
    bool PlanPresent,
    int Chapter,
    PlanCommitmentPeriod CommitmentPeriod,
    PlanFundingView Funding,
    string? TrusteePercentage,
    IReadOnlyList<PlanClassView> Classes,
    PlanUnsecuredView Unsecured,
    PlanFeasibilityView Feasibility,
    PlanLiquidationView Liquidation,
    PlanBestInterestsView BestInterests,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Problems)
{
    /// <summary>The view of <paramref name="calc"/>.</summary>
    /// <param name="calc">The engine's calculation.</param>
    /// <returns>The view.</returns>
    public static PlanCalculationView Of(PlanCalculation calc)
    {
        ArgumentNullException.ThrowIfNull(calc);
        var funding = calc.Funding;
        var feasibility = calc.Feasibility;
        var liquidation = calc.Liquidation;
        var best = calc.BestInterests;
        return new(
            calc.PlanPresent,
            calc.Chapter,
            new(calc.CommitmentMonths, calc.CommitmentSource),
            new(
                funding.TermMonths,
                funding.TermSource,
                M(funding.BasePayment),
                funding.BaseSource,
                [.. funding.Schedule.Select(item => new PlanScheduledPayment(item.Month, M(item.Payment)))],
                [.. funding.LumpSums.Select(item => new PlanLumpSum(item.Month, M(item.Amount), item.Description))],
                M(funding.Total)),
            Rate(calc.TrusteePercentage),
            [.. calc.Classes.Select(PlanClassView.Of)],
            new([.. calc.UnsecuredPool.Select(PlanPoolClaim.Of)], M(calc.UnsecuredPoolTotal), M(calc.UnsecuredTarget), calc.UnsecuredTargetSource, M(calc.UnsecuredPercentage)),
            new(
                feasibility.Feasible,
                M(feasibility.TotalFunding),
                M(feasibility.TotalDistributed),
                M(feasibility.Surplus),
                M(feasibility.Shortfall),
                feasibility.Reasons,
                feasibility.ScheduleJExcess is { } excess ? new PlanSourcedAmount(M(excess.Amount), excess.Source) : null,
                feasibility.ExceedsScheduleJ),
            new(
                [.. liquidation.Assets.Select(row => new PlanLiquidationAsset(row.AssetId, row.Description, M(row.Value), M(row.Liens), M(row.Exempt), M(row.Unexempt)))],
                M(liquidation.PropertyTotal),
                M(liquidation.LiensTotal),
                M(liquidation.ExemptionsTotal),
                M(liquidation.UnexemptTotal),
                M(liquidation.TrusteeCommission),
                liquidation.TrusteeCommissionRule,
                M(liquidation.OtherCosts),
                liquidation.OtherCostsSource,
                M(liquidation.PriorityTotal),
                M(liquidation.Available),
                [.. liquidation.Pool.Select(PlanPoolClaim.Of)],
                M(liquidation.PoolTotal),
                M(liquidation.Percentage)),
            new(M(best.PlanPercentage), M(best.LiquidationPercentage), M(best.PlanUnsecuredValue), Rate(best.PresentValueRate), best.Passes, best.Rule),
            calc.Warnings,
            calc.Problems);
    }

    // The engine's `_money`: ROUND_HALF_UP to the cent.
    internal static string M(decimal value) => Exact.Money(value, Rounding.HalfUp);

    internal static string? M(decimal? value) => value is { } present ? M(present) : null;

    // A rate is the decimal's own text, unrounded.
    internal static string? Rate(decimal? value) => value is { } present ? Exact.F(present) : null;
}

/// <summary>The applicable commitment period.</summary>
/// <param name="Months">36 or 60; null when not determined.</param>
/// <param name="Source">How it was determined.</param>
public sealed record PlanCommitmentPeriod(int? Months, string? Source);

/// <summary>What the plan pays in.</summary>
/// <param name="TermMonths">The plan's length; null when not entered.</param>
/// <param name="TermSource">Where the length came from.</param>
/// <param name="BasePayment">The monthly payment; null when not entered.</param>
/// <param name="BaseSource">Where the payment came from.</param>
/// <param name="Schedule">Step changes in the monthly payment.</param>
/// <param name="LumpSums">One-off payments.</param>
/// <param name="Total">Everything paid in over the term.</param>
public sealed record PlanFundingView(int? TermMonths, string? TermSource, string? BasePayment, string? BaseSource, IReadOnlyList<PlanScheduledPayment> Schedule, IReadOnlyList<PlanLumpSum> LumpSums, string Total);

/// <summary>A step in the monthly payment.</summary>
/// <param name="Month">The plan month it starts.</param>
/// <param name="Payment">The monthly payment from then.</param>
public sealed record PlanScheduledPayment(int Month, string Payment);

/// <summary>A one-off payment.</summary>
/// <param name="Month">The plan month it is paid.</param>
/// <param name="Amount">How much.</param>
/// <param name="Description">What it is; null when not entered.</param>
public sealed record PlanLumpSum(int Month, string Amount, string? Description);

/// <summary>One class of the payout.</summary>
/// <param name="Key">The class's key.</param>
/// <param name="Label">The class, for a person.</param>
/// <param name="Allowed">The allowed claims, summed.</param>
/// <param name="Principal">Principal paid.</param>
/// <param name="Interest">Interest paid.</param>
/// <param name="Payout">Principal and interest.</param>
/// <param name="Unpaid">Allowed but not paid.</param>
/// <param name="FirstMonth">The first month anything is paid; null when nothing is.</param>
/// <param name="LastMonth">The last month anything is paid; null when nothing is.</param>
/// <param name="Rows">The claims in the class.</param>
public sealed record PlanClassView(string Key, string Label, string Allowed, string Principal, string Interest, string Payout, string Unpaid, int? FirstMonth, int? LastMonth, IReadOnlyList<PlanPayoutRow> Rows)
{
    /// <summary>The view of <paramref name="summary"/>.</summary>
    /// <param name="summary">The class.</param>
    /// <returns>The view.</returns>
    public static PlanClassView Of(ClassSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new(
            summary.Key,
            summary.Label,
            PlanCalculationView.M(summary.Allowed),
            PlanCalculationView.M(summary.Principal),
            PlanCalculationView.M(summary.Interest),
            PlanCalculationView.M(summary.Principal + summary.Interest),
            PlanCalculationView.M(summary.Unpaid),
            summary.FirstMonth,
            summary.LastMonth,
            [.. summary.Rows.Select(PlanPayoutRow.Of)]);
    }
}

/// <summary>One claim's payout.</summary>
/// <param name="Key">The row's key.</param>
/// <param name="Label">The claim, for a person.</param>
/// <param name="ClaimId">The claim record; null for a row that is not a claim (the trustee, the attorney).</param>
/// <param name="Treatment">The plan's treatment of it; null when none.</param>
/// <param name="Allowed">The allowed amount.</param>
/// <param name="MonthlyPayment">A fixed monthly payment; null when none.</param>
/// <param name="Rate">The interest rate, percent; null when none.</param>
/// <param name="Principal">Principal paid.</param>
/// <param name="Interest">Interest paid.</param>
/// <param name="Payout">Principal and interest.</param>
/// <param name="FirstMonth">The first month paid; null when never.</param>
/// <param name="LastMonth">The last month paid; null when never.</param>
/// <param name="MonthsPaid">How many months it is paid in.</param>
/// <param name="Unpaid">Allowed but not paid.</param>
/// <param name="Source">How the row was computed.</param>
public sealed record PlanPayoutRow(
    string Key,
    string Label,
    string? ClaimId,
    string? Treatment,
    string Allowed,
    string? MonthlyPayment,
    string? Rate,
    string Principal,
    string Interest,
    string Payout,
    int? FirstMonth,
    int? LastMonth,
    int MonthsPaid,
    string Unpaid,
    string Source)
{
    /// <summary>The view of <paramref name="row"/>.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The view.</returns>
    public static PlanPayoutRow Of(PayoutRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(
            row.Key,
            row.Label,
            row.ClaimId,
            row.Treatment,
            PlanCalculationView.M(row.Allowed),
            PlanCalculationView.M(row.MonthlyPayment),
            PlanCalculationView.Rate(row.Rate),
            PlanCalculationView.M(row.Principal),
            PlanCalculationView.M(row.Interest),
            PlanCalculationView.M(row.Payout),
            row.FirstMonth,
            row.LastMonth,
            row.MonthsPaid,
            PlanCalculationView.M(row.Unpaid),
            row.Source);
    }
}

/// <summary>One general unsecured claim in a pool.</summary>
/// <param name="ClaimId">The claim record.</param>
/// <param name="Label">The claim, for a person.</param>
/// <param name="Amount">Its amount.</param>
/// <param name="Source">Where the amount came from.</param>
public sealed record PlanPoolClaim(string ClaimId, string Label, string Amount, string Source)
{
    /// <summary>The view of <paramref name="claim"/>.</summary>
    /// <param name="claim">The claim.</param>
    /// <returns>The view.</returns>
    public static PlanPoolClaim Of(PoolClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new(claim.ClaimId, claim.Label, PlanCalculationView.M(claim.Amount), claim.Source);
    }
}

/// <summary>The general unsecured pool and what the plan gives it.</summary>
/// <param name="Pool">The claims.</param>
/// <param name="PoolTotal">Their amounts, summed.</param>
/// <param name="Target">What the plan pays the pool; null when not derivable.</param>
/// <param name="TargetSource">How the target was set.</param>
/// <param name="Percentage">Target over total, percent; null when not derivable.</param>
public sealed record PlanUnsecuredView(IReadOnlyList<PlanPoolClaim> Pool, string PoolTotal, string? Target, string? TargetSource, string? Percentage);

/// <summary>Whether the funding covers the payout.</summary>
/// <param name="Feasible">The answer; null when it cannot be told yet.</param>
/// <param name="TotalFunding">Everything paid in.</param>
/// <param name="TotalDistributed">Everything paid out, the trustee's fee included.</param>
/// <param name="Surplus">Funding left over.</param>
/// <param name="Shortfall">Payout not funded.</param>
/// <param name="Reasons">Why it is not feasible, for a person.</param>
/// <param name="ScheduleJExcess">Schedule J's monthly net income; null when not derivable.</param>
/// <param name="ExceedsScheduleJ">Whether the payment exceeds it; null when not derivable.</param>
public sealed record PlanFeasibilityView(bool? Feasible, string TotalFunding, string TotalDistributed, string Surplus, string Shortfall, IReadOnlyList<string> Reasons, PlanSourcedAmount? ScheduleJExcess, bool? ExceedsScheduleJ);

/// <summary>An amount and where it came from.</summary>
/// <param name="Amount">The amount.</param>
/// <param name="Source">Where it came from.</param>
public sealed record PlanSourcedAmount(string Amount, string Source);

/// <summary>A hypothetical Chapter 7 liquidation.</summary>
/// <param name="Assets">Each asset's value, liens and exemptions.</param>
/// <param name="PropertyTotal">The assets' value, summed.</param>
/// <param name="LiensTotal">The liens, summed.</param>
/// <param name="ExemptionsTotal">The exemptions, summed.</param>
/// <param name="UnexemptTotal">The unexempt equity, summed.</param>
/// <param name="TrusteeCommission">The Chapter 7 trustee's § 326 commission.</param>
/// <param name="TrusteeCommissionRule">How the commission was computed.</param>
/// <param name="OtherCosts">Other administrative costs.</param>
/// <param name="OtherCostsSource">Where they came from.</param>
/// <param name="PriorityTotal">Priority claims paid first.</param>
/// <param name="Available">Left for general unsecured creditors.</param>
/// <param name="Pool">The general unsecured claims.</param>
/// <param name="PoolTotal">Their amounts, summed.</param>
/// <param name="Percentage">What they would receive, percent; null when the pool is empty.</param>
public sealed record PlanLiquidationView(
    IReadOnlyList<PlanLiquidationAsset> Assets,
    string PropertyTotal,
    string LiensTotal,
    string ExemptionsTotal,
    string UnexemptTotal,
    string TrusteeCommission,
    string TrusteeCommissionRule,
    string OtherCosts,
    string OtherCostsSource,
    string PriorityTotal,
    string Available,
    IReadOnlyList<PlanPoolClaim> Pool,
    string PoolTotal,
    string? Percentage);

/// <summary>One asset in the liquidation.</summary>
/// <param name="AssetId">The asset record.</param>
/// <param name="Description">The asset, as described.</param>
/// <param name="Value">Its value.</param>
/// <param name="Liens">The liens on it.</param>
/// <param name="Exempt">What is exempt.</param>
/// <param name="Unexempt">What a trustee could sell.</param>
public sealed record PlanLiquidationAsset(string AssetId, string? Description, string Value, string Liens, string Exempt, string Unexempt);

/// <summary>§ 1325(a)(4): unsecured creditors get at least what Chapter 7 would give them.</summary>
/// <param name="PlanPercentage">What the plan pays them, percent.</param>
/// <param name="LiquidationPercentage">What liquidation would, percent.</param>
/// <param name="PlanUnsecuredValue">The plan's unsecured payout, at present value.</param>
/// <param name="PresentValueRate">The discount rate, percent; null when none.</param>
/// <param name="Passes">The answer; null when it cannot be told yet.</param>
/// <param name="Rule">How it was decided.</param>
public sealed record PlanBestInterestsView(string? PlanPercentage, string? LiquidationPercentage, string PlanUnsecuredValue, string? PresentValueRate, bool? Passes, string Rule);
