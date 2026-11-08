using Insolvia.Engines.Liens;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Projections.Forms;

namespace Insolvia.Forms;

/// <summary>
/// The money, as decimals (from <c>case_summary.CaseTotals</c>). Every
/// figure is the SAME function the official form prints from — so a figure
/// here cannot disagree with the schedule it summarises.
/// </summary>
public sealed record CaseTotals(
    decimal RealEstate,
    decimal PersonalProperty,
    decimal Assets,
    decimal TotalExempt,
    decimal TotalNonExempt,
    decimal Secured,
    decimal PriorityUnsecured,
    decimal NonpriorityUnsecured,
    decimal Liabilities,
    decimal MonthlyIncome,
    decimal MonthlyExpenses,
    decimal MonthlyExcess);

/// <summary>One case, from above: totals, the completeness gate's problems, and the lien figures.</summary>
public sealed record CaseSummary(CaseTotals Totals, IReadOnlyList<PacketProblem> Problems, LienFigures Liens)
{
    /// <summary>Whether this case could assemble its packet today.</summary>
    public bool ReadyToFile => Problems.Count == 0;
}

/// <summary>What a case looks like from above (from <c>core/case_summary.py</c>).</summary>
public static class CaseSummaries
{
    /// <summary>
    /// The summary of an already-loaded case: the schedules' own totals, and
    /// readiness by the SAME completeness gate packet assembly runs.
    /// </summary>
    public static CaseSummary Summarise(CaseData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var caseFile = data.ToCaseFile();
        var realEstate = B106AB.RealEstateTotal(caseFile);
        var personalProperty = B106AB.PersonalPropertyTotal(caseFile);
        var assets = realEstate + personalProperty;

        // The plain sum of the exemption claims' amounts — not the exemption
        // law's arithmetic (issue #346's), and an absent amount is zero.
        var totalExempt = caseFile.Exemptions.Aggregate(0m, (sum, exemption) => sum + ProjectionHelpers.Amount(exemption.Amount));
        var secured = B106D.SecuredTotal(caseFile);
        var priority = B106EF.PriorityUnsecuredTotal(caseFile);
        var nonpriority = B106EF.NonpriorityUnsecuredTotal(caseFile);
        var monthlyIncome = B106I.MonthlyIncomeLine12(caseFile);
        var monthlyExpenses = B106J.MonthlyExpensesLine22c(caseFile);
        return new CaseSummary(
            new CaseTotals(
                realEstate,
                personalProperty,
                assets,
                totalExempt,
                assets - totalExempt,
                secured,
                priority,
                nonpriority,
                secured + priority + nonpriority,
                monthlyIncome,
                monthlyExpenses,
                monthlyIncome - monthlyExpenses),
            PacketAssembly.CompletenessProblems(data),
            LienEngine.Derive(caseFile));
    }
}
