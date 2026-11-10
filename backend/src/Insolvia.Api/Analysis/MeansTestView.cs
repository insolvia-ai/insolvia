using System.Text.Json.Serialization;
using Insolvia.Domain.Engines;
using Insolvia.Domain.Engines.MeansTest;

namespace Insolvia.Api.Analysis;

/// <summary>
/// The § 707(b) means test as of the expected filing date: the inputs as the
/// engine read them, the outcome when one was reached, and every reason it
/// was not. Money is a decimal string; a figure that could not be derived is null.
/// </summary>
/// <param name="AsOf">The date the test is run as of, <c>YYYY-MM-DD</c>.</param>
/// <param name="AsOfSource">Where that date came from.</param>
/// <param name="Chapter">7 or 13.</param>
/// <param name="ReleaseIds">The regulatory release used, by data series; empty when none covers the date.</param>
/// <param name="Jurisdiction">Where the debtor lives, as the medians and standards read it.</param>
/// <param name="MaritalFilingStatus">B122A-1 line 1.</param>
/// <param name="Household">The household sizes the test uses.</param>
/// <param name="Exemptions">The presumption exemptions the case claims, and which one applied.</param>
/// <param name="Cmi">The current monthly income derivation.</param>
/// <param name="Debt">The debt totals the Chapter 7 test reads.</param>
/// <param name="Comparison">The median comparison; null when it could not be made.</param>
/// <param name="Outcome">The determination, or <c>undetermined</c> when the engine refused.</param>
/// <param name="DeterminedBy">Which step decided the outcome; null when undetermined.</param>
/// <param name="Lines">Every computed form line, in form order.</param>
/// <param name="Chapter13">The Chapter 13 commitment figures; null for Chapter 7 or when undetermined.</param>
/// <param name="Problems">Everything missing, for a person; empty when nothing is.</param>
public sealed record MeansTestView(
    string AsOf,
    string AsOfSource,
    int Chapter,
    IReadOnlyDictionary<string, string> ReleaseIds,
    MeansTestJurisdiction Jurisdiction,
    MeansTestMaritalStatus MaritalFilingStatus,
    MeansTestHousehold Household,
    MeansTestExemptions Exemptions,
    CmiView Cmi,
    MeansTestDebt Debt,
    MedianComparisonView? Comparison,
    string Outcome,
    string? DeterminedBy,
    IReadOnlyList<MeansTestLineView> Lines,
    MeansTestChapter13? Chapter13,
    IReadOnlyList<string> Problems)
{
    /// <summary>The view of <paramref name="trace"/>.</summary>
    /// <param name="trace">The engine's trace.</param>
    /// <returns>The view.</returns>
    public static MeansTestView Of(MeansTestTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var test = trace.Case;
        var inputs = test.Inputs;
        var exemption = test.Chapter == 7 ? MeansTestEngine.PresumptionExemption(inputs) : null;
        var result = trace.Result;
        return new(
            Exact.Iso(trace.AsOf),
            trace.AsOfSource,
            test.Chapter,
            trace.ReleaseIds,
            new(string.IsNullOrEmpty(test.State) ? null : test.State, string.IsNullOrEmpty(test.County) ? null : test.County, test.District),
            new(trace.MaritalFilingStatus, trace.MaritalSource),
            new(
                inputs.PeopleUnder65,
                inputs.People65OrOlder,
                MeansTestHouseholdSize.Of(MeansTestEngine.MedianHouseholdSize(inputs)),
                MeansTestHouseholdSize.Of(MeansTestEngine.IrsFamilySize(inputs)),
                MeansTestHouseholdSize.Of(MeansTestEngine.IrsHousingFamilySize(inputs)),
                test.ChildrenUnder18),
            new(
                inputs.NonConsumerDebts == true,
                inputs.DisabledVeteran == true,
                inputs.ReservistNationalGuard == true,
                exemption?.Flag,
                exemption?.Rule,
                [.. MeansTestEngine.PresumptionExemptions.Select(item => item.Flag)]),
            CmiView.Of(test.Cmi),
            new(test.PriorityDebtTotal, test.NonpriorityUnsecuredTotal),
            result?.Comparison is { } comparison ? MedianComparisonView.Of(comparison) : null,
            result is not null ? result.Outcome : "undetermined",
            result?.DeterminedBy,
            [.. (result?.Lines ?? []).Select(line => new MeansTestLineView(line.Form, line.Line, line.Label, line.Amount, line.Source))],
            result is { Chapter: 13 } ? MeansTestChapter13.Of(result) : null,
            trace.Problems);
    }
}

/// <summary>Where the debtor lives.</summary>
/// <param name="State">Two-letter state; null when not on file.</param>
/// <param name="County">County; null when not on file.</param>
/// <param name="District">The bankruptcy district.</param>
public sealed record MeansTestJurisdiction(string? State, string? County, string District);

/// <summary>B122A-1 line 1.</summary>
/// <param name="Value">The status; null when it cannot be told yet.</param>
/// <param name="Source">Where it came from, or why it is missing.</param>
public sealed record MeansTestMaritalStatus(string? Value, string Source);

/// <summary>The household as the test counts it.</summary>
/// <param name="PeopleUnder65">People under 65, as entered.</param>
/// <param name="People65OrOlder">People 65 or older, as entered.</param>
/// <param name="MedianHouseholdSize">The size the state median is looked up for.</param>
/// <param name="IrsFamilySize">The family size for the IRS National Standards.</param>
/// <param name="IrsHousingFamilySize">The family size for the IRS housing standards.</param>
/// <param name="ChildrenUnder18">Dependents under 18.</param>
public sealed record MeansTestHousehold(int? PeopleUnder65, int? People65OrOlder, MeansTestHouseholdSize MedianHouseholdSize, MeansTestHouseholdSize IrsFamilySize, MeansTestHouseholdSize IrsHousingFamilySize, int ChildrenUnder18);

/// <summary>A household size and where it came from.</summary>
/// <param name="Value">The size; null when it cannot be derived yet.</param>
/// <param name="Source">Where it came from; null with the size.</param>
public sealed record MeansTestHouseholdSize(int? Value, string? Source)
{
    /// <summary>The view of an engine size.</summary>
    /// <param name="size">The size, if derivable.</param>
    /// <returns>The view.</returns>
    public static MeansTestHouseholdSize Of((int Size, string Source)? size) => size is { } value ? new(value.Size, value.Source) : new(null, null);
}

/// <summary>The § 707(b)(2)(D) and § 707(b)(2)(A) exemptions from the presumption.</summary>
/// <param name="NonConsumerDebts">Debts are not primarily consumer debts.</param>
/// <param name="DisabledVeteran">The disabled-veteran exemption is claimed.</param>
/// <param name="ReservistNationalGuard">The reservist / National Guard exemption is claimed.</param>
/// <param name="Applied">The exemption that applied (Chapter 7 only); null when none did.</param>
/// <param name="Rule">The rule behind <paramref name="Applied"/>.</param>
/// <param name="Available">Every exemption flag the test knows.</param>
public sealed record MeansTestExemptions(bool NonConsumerDebts, bool DisabledVeteran, bool ReservistNationalGuard, string? Applied, string? Rule, IReadOnlyList<string> Available);

/// <summary>The debt totals.</summary>
/// <param name="PriorityTotal">Priority unsecured debt.</param>
/// <param name="NonpriorityUnsecuredTotal">Nonpriority unsecured debt.</param>
public sealed record MeansTestDebt(string PriorityTotal, string NonpriorityUnsecuredTotal);

/// <summary>The current monthly income (§ 101(10A)) derivation.</summary>
/// <param name="Window">The six-month look-back.</param>
/// <param name="Columns">One B122A-1 column per earner.</param>
/// <param name="CombinedMonthlyTotal">The columns' monthly totals, summed.</param>
/// <param name="Annualized">The combined total times twelve.</param>
/// <param name="Gaps">Months an employment looks to have paid nothing.</param>
/// <param name="Problems">What is missing from the derivation.</param>
public sealed record CmiView(CmiWindowView Window, IReadOnlyList<CmiColumnView> Columns, string CombinedMonthlyTotal, string Annualized, IReadOnlyList<CmiGapView> Gaps, IReadOnlyList<string> Problems)
{
    /// <summary>The view of <paramref name="cmi"/>.</summary>
    /// <param name="cmi">The derivation.</param>
    /// <returns>The view.</returns>
    public static CmiView Of(CmiResult cmi)
    {
        ArgumentNullException.ThrowIfNull(cmi);
        return new(
            new(Exact.Iso(cmi.Window.FilingDate), Exact.Iso(cmi.Window.Start), Exact.Iso(cmi.Window.End), cmi.Window.Months),
            [.. cmi.Columns.Select(column => new CmiColumnView(column.Column, [.. column.Lines.Select(CmiLineView.Of)], [.. column.Excluded.Select(CmiLineView.Of)], column.MonthlyTotal))],
            cmi.CombinedMonthlyTotal,
            cmi.Annualized,
            [.. cmi.Gaps.Select(gap => new CmiGapView(gap.Employer, gap.Months))],
            cmi.Problems);
    }
}

/// <summary>The six full months before the filing date.</summary>
/// <param name="FilingDate">The filing date the window is counted back from.</param>
/// <param name="Start">The first day of the window.</param>
/// <param name="End">The last day of the window.</param>
/// <param name="Months">The window's months, <c>YYYY-MM</c>.</param>
public sealed record CmiWindowView(string FilingDate, string Start, string End, IReadOnlyList<string> Months);

/// <summary>One B122A-1 column: A is debtor 1, B debtor 2 or a non-filing spouse.</summary>
/// <param name="Column">A or B.</param>
/// <param name="Lines">The income lines counted.</param>
/// <param name="Excluded">Lines § 101(10A)(B)(ii) excludes.</param>
/// <param name="MonthlyTotal">The counted lines' monthly averages, summed.</param>
public sealed record CmiColumnView(string Column, IReadOnlyList<CmiLineView> Lines, IReadOnlyList<CmiLineView> Excluded, string MonthlyTotal);

/// <summary>One income line of a column.</summary>
/// <param name="Category">The line's income category.</param>
/// <param name="Label">The line, for a person.</param>
/// <param name="TotalReceived">Received inside the window.</param>
/// <param name="MonthlyAverage">The total over six.</param>
/// <param name="Citation">The rule that applies; empty when none.</param>
/// <param name="Note">How the line was made; empty when ordinary.</param>
/// <param name="Entries">Every receipt behind the line.</param>
/// <param name="GrossMonthlyAverage">Business or rental gross receipts, monthly; absent for other lines.</param>
/// <param name="ExpensesMonthlyAverage">Business or rental expenses, monthly; absent for other lines.</param>
public sealed record CmiLineView(
    string Category,
    string Label,
    string TotalReceived,
    string MonthlyAverage,
    string Citation,
    string Note,
    IReadOnlyList<CmiEntryView> Entries,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GrossMonthlyAverage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpensesMonthlyAverage)
{
    /// <summary>The view of <paramref name="line"/>.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The view.</returns>
    public static CmiLineView Of(CmiLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new(line.Category, line.Label, line.TotalReceived, line.MonthlyAverage, line.Citation, line.Note, [.. line.Entries.Select(entry => new CmiEntryView(entry.ReceivedOn, entry.Amount, entry.Description))], line.GrossMonthlyAverage, line.ExpensesMonthlyAverage);
    }
}

/// <summary>One dated receipt.</summary>
/// <param name="ReceivedOn">When it was received.</param>
/// <param name="Amount">How much.</param>
/// <param name="Description">What it was.</param>
public sealed record CmiEntryView(string ReceivedOn, string Amount, string Description);

/// <summary>Window months with no paycheck from an employment that looks like it should have produced one.</summary>
/// <param name="Employer">The employer.</param>
/// <param name="Months">The months, <c>YYYY-MM</c>.</param>
public sealed record CmiGapView(string Employer, IReadOnlyList<string> Months);

/// <summary>Current monthly income against the state median.</summary>
/// <param name="State">The state whose median applies.</param>
/// <param name="HouseholdSize">The household size it is looked up for.</param>
/// <param name="MonthlyCmi">Current monthly income.</param>
/// <param name="AnnualizedCmi">Twelve times that.</param>
/// <param name="AnnualMedian">The applicable median family income.</param>
/// <param name="AboveMedian">Whether the annualized income is above it.</param>
/// <param name="Source">The median's release.</param>
public sealed record MedianComparisonView(string State, int HouseholdSize, string MonthlyCmi, string AnnualizedCmi, string AnnualMedian, bool AboveMedian, string Source)
{
    /// <summary>The view of <paramref name="comparison"/>.</summary>
    /// <param name="comparison">The comparison.</param>
    /// <returns>The view.</returns>
    public static MedianComparisonView Of(MedianComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return new(comparison.State, comparison.HouseholdSize, comparison.MonthlyCmi, comparison.AnnualizedCmi, comparison.AnnualMedian, comparison.AboveMedian, comparison.Source);
    }
}

/// <summary>One computed line of the means-test forms.</summary>
/// <param name="Form">The form, e.g. <c>122A-2</c>.</param>
/// <param name="Line">The line number.</param>
/// <param name="Label">The line, for a person.</param>
/// <param name="Amount">The figure.</param>
/// <param name="Source">How it was computed.</param>
public sealed record MeansTestLineView(string Form, string Line, string Label, string Amount, string Source);

/// <summary>B122C-1's commitment period and disposable income.</summary>
/// <param name="CommitmentPeriodMonths">36 or 60.</param>
/// <param name="CommitmentSource">The comparison that set it.</param>
/// <param name="CommitmentMaritalAdjustment">The marital adjustment deducted.</param>
/// <param name="CommitmentMonthlyIncome">The monthly income compared.</param>
/// <param name="CommitmentAnnualizedIncome">Twelve times that.</param>
/// <param name="CommitmentAnnualMedian">The applicable median.</param>
/// <param name="DisposableIncomeRequired">Whether B122C-2 applies (above median).</param>
/// <param name="MonthlyDisposableIncome">B122C-2's monthly disposable income; null when not computed.</param>
public sealed record MeansTestChapter13(
    int CommitmentPeriodMonths,
    string CommitmentSource,
    string CommitmentMaritalAdjustment,
    string CommitmentMonthlyIncome,
    string CommitmentAnnualizedIncome,
    string CommitmentAnnualMedian,
    bool DisposableIncomeRequired,
    string? MonthlyDisposableIncome)
{
    /// <summary>The Chapter 13 figures of <paramref name="result"/>.</summary>
    /// <param name="result">A Chapter 13 result.</param>
    /// <returns>The view.</returns>
    public static MeansTestChapter13 Of(MeansTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var commitment = result.Commitment!;
        return new(commitment.Months, commitment.Source, commitment.MaritalAdjustment, commitment.MonthlyIncome, commitment.AnnualizedIncome, commitment.AnnualMedian, result.Outcome == "above_median", result.DisposableIncome);
    }
}
