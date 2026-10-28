using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;
using Insolvia.Core.Income;
using Insolvia.Core.MeansTest;
using Insolvia.Engines.MeansTest;
using Insolvia.Engines.Tests.Support;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_cmi.py: known-answer checks
// for the § 101(10A) CMI derivation — arithmetic a reviewer can redo on paper.
public sealed class CmiTests
{
    private static readonly Debtor Debtor1 = Build.Debtor(FilingRole.Debtor1, "d-1");
    private static readonly Debtor Debtor2 = Build.Debtor(FilingRole.Debtor2, "d-2");
    private static readonly EmploymentBody Employment = Job("d-1", EmploymentStatus.Employed, "Acme Staffing");

    // --- the window ---

    [Theory]
    [InlineData("2026-09-15", "2026-03-01", "2026-08-31", "2026-03", "2026-08")]
    [InlineData("2026-09-01", "2026-03-01", "2026-08-31", "2026-03", "2026-08")]
    [InlineData("2026-02-10", "2025-08-01", "2026-01-31", "2025-08", "2026-01")]
    [InlineData("2026-01-02", "2025-07-01", "2025-12-31", "2025-07", "2025-12")]
    public void The_window_is_six_whole_calendar_months(string filing, string start, string end, string firstMonth, string lastMonth)
    {
        var window = Cmi.Window(Py.ParseIso(filing)!.Value);
        Assert.Equal(Py.ParseIso(start), window.Start);
        Assert.Equal(Py.ParseIso(end), window.End);
        Assert.Equal(6, window.Months.Count);
        Assert.Equal(firstMonth, window.Months[0]);
        Assert.Equal(lastMonth, window.Months[^1]);
    }

    // --- known-answer scenarios ---

    [Fact]
    public void A_regular_monthly_salary_averages_to_itself()
    {
        var result = Compute(payPeriods: Monthly(5200.00m));
        var column = Assert.Single(result.Columns);
        Assert.Equal("A", column.Column);
        var wages = Assert.Single(column.Lines);
        Assert.Equal("31200.00", wages.TotalReceived);
        Assert.Equal("5200.00", wages.MonthlyAverage);
        Assert.Equal(6, wages.Entries.Count);
        Assert.Equal("5200.00", column.MonthlyTotal);
        Assert.Equal("5200.00", result.CombinedMonthlyTotal);
        Assert.Equal("62400.00", result.Annualized);
        Assert.Empty(result.Gaps);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Biweekly_pay_counts_thirteen_checks_by_pay_date()
    {
        var payDates = new List<DateOnly>();
        for (var current = new DateOnly(2026, 3, 6); current <= new DateOnly(2026, 8, 31); current = current.AddDays(14))
        {
            payDates.Add(current);
        }

        Assert.Equal(13, payDates.Count);
        var result = Compute(payPeriods: [.. payDates.Select(day => Paycheck(day, 2000.00m))]);
        var wages = Assert.Single(Assert.Single(result.Columns).Lines);
        Assert.Equal("26000.00", wages.TotalReceived);
        Assert.Equal("4333.33", wages.MonthlyAverage);
        Assert.Equal("4333.33", result.CombinedMonthlyTotal);
    }

    [Fact]
    public void A_mid_window_job_change_surfaces_the_gap_months()
    {
        var oldJob = Job("d-1", EmploymentStatus.NotEmployed, "Old Employer");
        var newJob = Job("d-1", EmploymentStatus.Employed, "New Employer") with { EmployedSince = new DateOnly(2026, 6, 15) };
        var result = Compute(
            employments: [("em-old", oldJob), ("em-new", newJob)],
            payPeriods:
            [
                Paycheck(new DateOnly(2026, 3, 31), 4000.00m, "em-old"),
                Paycheck(new DateOnly(2026, 4, 30), 4000.00m, "em-old"),
                Paycheck(new DateOnly(2026, 6, 30), 4500.00m, "em-new"),
                Paycheck(new DateOnly(2026, 7, 31), 4500.00m, "em-new"),
                Paycheck(new DateOnly(2026, 8, 31), 4500.00m, "em-new"),
            ]);
        var wages = Assert.Single(Assert.Single(result.Columns).Lines);
        Assert.Equal("21500.00", wages.TotalReceived);
        Assert.Equal("3583.33", wages.MonthlyAverage);
        var byEmployer = result.Gaps.ToDictionary(gap => gap.Employer, gap => gap.Months);
        Assert.Equal(["2026-05", "2026-06", "2026-07", "2026-08"], byEmployer["Old Employer"]);
        Assert.DoesNotContain("New Employer", byEmployer.Keys);
    }

    [Fact]
    public void Non_wage_income_lands_on_its_lines_and_exclusions_are_shown()
    {
        var receipts = Enumerable.Range(3, 6)
            .Select(month => Receipt(OtherIncomeCategory.Unemployment, new DateOnly(2026, month, 7), 1200.00m, payer: "State Agency"))
            .Append(Receipt(OtherIncomeCategory.SocialSecurityActBenefit, new DateOnly(2026, 5, 3), 1900.00m))
            .ToList();
        var result = Compute(employments: [], otherIncome: receipts);
        var column = Assert.Single(result.Columns);
        var unemployment = Assert.Single(column.Lines);
        Assert.Equal("unemployment", unemployment.Category);
        Assert.Equal("1200.00", unemployment.MonthlyAverage);
        var excluded = Assert.Single(column.Excluded);
        Assert.Equal("social_security_act_benefit", excluded.Category);
        Assert.Equal("1900.00", excluded.TotalReceived);
        Assert.Equal(Cmi.ExclusionCitation, excluded.Citation);
        Assert.Equal("1200.00", column.MonthlyTotal);
        Assert.Equal("1200.00", result.CombinedMonthlyTotal);
    }

    [Fact]
    public void Business_receipts_net_their_expenses_with_a_zero_floor()
    {
        var result = Compute(
            employments: [],
            otherIncome:
            [
                Receipt(OtherIncomeCategory.Business, new DateOnly(2026, 4, 15), 3000.00m, expenses: 1200.00m),
                Receipt(OtherIncomeCategory.Rental, new DateOnly(2026, 5, 1), 1000.00m, expenses: 1600.00m),
            ]);
        var column = Assert.Single(result.Columns);
        Assert.Equal(2, column.Lines.Count);
        var (business, rental) = (column.Lines[0], column.Lines[1]);
        Assert.Equal("1800.00", business.TotalReceived);
        Assert.Contains("less ordinary and necessary operating expenses", business.Note, StringComparison.Ordinal);
        Assert.Equal("0.00", rental.TotalReceived);
        Assert.Contains("enters as zero", rental.Note, StringComparison.Ordinal);
        Assert.Equal("300.00", column.MonthlyTotal);
    }

    [Fact]
    public void Receipts_outside_the_window_do_not_count()
    {
        var result = Compute(payPeriods:
        [
            Paycheck(new DateOnly(2026, 2, 28), 9999.00m),
            Paycheck(new DateOnly(2026, 9, 1), 9999.00m),
            Paycheck(new DateOnly(2026, 8, 31), 5000.00m),
            Paycheck(new DateOnly(2026, 3, 1), 5000.00m),
        ]);
        Assert.Equal("10000.00", Assert.Single(Assert.Single(result.Columns).Lines).TotalReceived);
    }

    [Fact]
    public void A_spouse_takes_column_b_and_both_combine()
    {
        var spouseJob = Job("d-2", EmploymentStatus.Employed, "Spouse Employer");
        var result = Compute(
            debtors: [Debtor1, Debtor2],
            employments: [("em-1", Employment), ("em-2", spouseJob)],
            payPeriods: [.. Monthly(3000.00m), .. Monthly(1500.00m, "em-2")]);
        Assert.Equal(["A", "B"], result.Columns.Select(column => column.Column));
        Assert.Equal("3000.00", result.Columns[0].MonthlyTotal);
        Assert.Equal("1500.00", result.Columns[1].MonthlyTotal);
        Assert.Equal("4500.00", result.CombinedMonthlyTotal);
        Assert.Equal("54000.00", result.Annualized);
    }

    [Fact]
    public void A_non_filing_spouse_also_takes_column_b()
    {
        var spouse = Build.Debtor(FilingRole.NonFilingSpouse, "d-3");
        var result = Compute(
            debtors: [Debtor1, spouse],
            employments: [],
            otherIncome: [Receipt(OtherIncomeCategory.PensionRetirement, new DateOnly(2026, 4, 1), 600.00m, debtorId: "d-3")]);
        var column = Assert.Single(result.Columns);
        Assert.Equal("B", column.Column);
        Assert.Equal("100.00", column.MonthlyTotal);
    }

    [Fact]
    public void Unplaceable_records_become_problems_not_zeros()
    {
        var result = Compute(
            payPeriods:
            [
                Paycheck(new DateOnly(2026, 4, 10), 1000.00m, "em-unknown"),
                new PayPeriodRecordBody("em-1", null, null, null, 1000.00m, null, null, []),
                new PayPeriodRecordBody("em-1", null, null, new DateOnly(2026, 5, 8), null, null, null, []),
            ],
            otherIncome:
            [
                new OtherIncomeRecordBody(null, OtherIncomeCategory.Unemployment, null, 100.00m, null, null, null),
                new OtherIncomeRecordBody(null, OtherIncomeCategory.Unemployment, new DateOnly(2026, 4, 1), 100.00m, null, null, null),
            ]);
        Assert.Equal(5, result.Problems.Count);
        foreach (var fragment in new[] { "no existing employment", "no pay date", "no gross amount", "no received-on date", "names no debtor" })
        {
            Assert.Contains(result.Problems, problem => problem.Contains(fragment, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_line_derivation_is_traceable_entry_by_entry()
    {
        var result = Compute(
            payPeriods: Monthly(5200.00m),
            otherIncome: [Receipt(OtherIncomeCategory.InterestDividendsRoyalties, new DateOnly(2026, 6, 30), 90.00m)]);
        foreach (var line in result.Columns.SelectMany(column => column.Lines).Where(line => line.Note.Length == 0))
        {
            Assert.Equal(Py.Dec(line.TotalReceived), line.Entries.Sum(entry => Py.Dec(entry.Amount)));
        }

        Assert.Equal("5215.00", result.CombinedMonthlyTotal);
    }

    // --- entered overrides (issue #349) ---

    [Fact]
    public void An_override_replaces_the_derived_line_and_says_so()
    {
        var result = Compute(payPeriods: Monthly(5200.00m), overrides: [Override(IncomeColumn.A, IncomeLineCategory.Wages, 6000.00m)]);
        var column = Assert.Single(result.Columns);
        var wages = Assert.Single(column.Lines);
        Assert.Equal("6000.00", wages.MonthlyAverage);
        Assert.Equal("36000.00", wages.TotalReceived);
        Assert.Empty(wages.Entries);
        Assert.Equal(Cmi.OverrideNote, wages.Note);
        Assert.Equal("6000.00", column.MonthlyTotal);
        Assert.Equal("6000.00", result.CombinedMonthlyTotal);
    }

    [Fact]
    public void An_override_creates_a_line_the_records_never_produced()
    {
        var result = Compute(payPeriods: Monthly(5200.00m), overrides: [Override(IncomeColumn.A, IncomeLineCategory.PensionRetirement, 300.00m)]);
        var column = Assert.Single(result.Columns);
        Assert.Equal(["wages", "pension_retirement"], column.Lines.Select(line => line.Category));
        Assert.Equal("5500.00", column.MonthlyTotal);
    }

    [Fact]
    public void An_override_for_the_other_column_leaves_this_one_alone()
    {
        var result = Compute(payPeriods: Monthly(5200.00m), overrides: [Override(IncomeColumn.B, IncomeLineCategory.Wages, 6000.00m)]);
        var columns = result.Columns.ToDictionary(column => column.Column);
        Assert.Equal("5200.00", columns["A"].MonthlyTotal);
        Assert.Equal("6000.00", columns["B"].MonthlyTotal);
        Assert.Equal("11200.00", result.CombinedMonthlyTotal);
    }

    [Fact]
    public void An_excluded_kind_can_be_entered_and_stays_uncounted()
    {
        var result = Compute(payPeriods: Monthly(5200.00m), overrides: [Override(IncomeColumn.A, IncomeLineCategory.SocialSecurityActBenefit, 900.00m)]);
        var column = Assert.Single(result.Columns);
        var excluded = Assert.Single(column.Excluded);
        Assert.Equal("social_security_act_benefit", excluded.Category);
        Assert.Equal("900.00", excluded.MonthlyAverage);
        Assert.Equal(Cmi.ExclusionCitation, excluded.Citation);
        Assert.Equal("5200.00", column.MonthlyTotal);
    }

    [Fact]
    public void Unemployment_claimed_as_ssa_moves_out_of_line_8()
    {
        var receipts = Enumerable.Range(3, 6).Select(month => Receipt(OtherIncomeCategory.Unemployment, new DateOnly(2026, month, 7), 1200.00m)).ToList();
        var result = Compute(employments: [], otherIncome: receipts, overrides: [Override(IncomeColumn.A, IncomeLineCategory.UnemploymentAsSsa, 500.00m)]);
        var column = Assert.Single(result.Columns);
        var unemployment = Assert.Single(column.Lines);
        Assert.Equal("700.00", unemployment.MonthlyAverage);
        Assert.Contains("Social Security Act", unemployment.Note, StringComparison.Ordinal);
        var contended = Assert.Single(column.Excluded);
        Assert.Equal("unemployment_as_ssa", contended.Category);
        Assert.Equal("500.00", contended.MonthlyAverage);
        Assert.Equal(Cmi.ExclusionCitation, contended.Citation);
        Assert.Equal("700.00", column.MonthlyTotal);
    }

    [Fact]
    public void An_incomplete_override_row_is_ignored()
    {
        var result = Compute(payPeriods: Monthly(5200.00m), overrides: [new IncomeLineOverride("half", IncomeColumn.A, IncomeLineCategory.Wages, null)]);
        Assert.Equal("5200.00", result.CombinedMonthlyTotal);
    }

    // --- beyond the Python suite: the two rounding modes at the .xx5 boundary ---

    [Fact]
    public void A_monthly_average_on_a_half_cent_rounds_half_up()
    {
        // 6 x 1000.005 is not a cent figure, so build the half cent from the
        // division: 6000.03 / 6 = 1000.005 → 1000.01 (ROUND_HALF_UP).
        var result = Compute(payPeriods: [Paycheck(new DateOnly(2026, 4, 1), 6000.03m)]);
        Assert.Equal("1000.01", Assert.Single(result.Columns).MonthlyTotal);
        Assert.Equal("1000.01", result.CombinedMonthlyTotal);
    }

    [Fact]
    public void The_annualized_figure_is_twelve_times_the_rounded_monthly()
    {
        var result = Compute(payPeriods: [Paycheck(new DateOnly(2026, 4, 1), 6000.03m)]);
        Assert.Equal("12000.12", result.Annualized);
    }

    private static EmploymentBody Job(string debtorId, EmploymentStatus status, string employer) =>
        new(debtorId, status, null, employer, new Address(), null);

    private static PayPeriodRecordBody Paycheck(DateOnly payDate, decimal gross, string employmentId = "em-1") =>
        new(employmentId, null, null, payDate, gross, null, PayFrequency.Monthly, []);

    private static List<PayPeriodRecordBody> Monthly(decimal gross, string employmentId = "em-1") =>
        [.. Enumerable.Range(3, 6).Select(month => Paycheck(new DateOnly(2026, month, 25), gross, employmentId))];

    private static OtherIncomeRecordBody Receipt(OtherIncomeCategory category, DateOnly receivedOn, decimal amount, decimal? expenses = null, string? payer = null, string debtorId = "d-1") =>
        new(debtorId, category, receivedOn, amount, expenses, payer, null);

    private static IncomeLineOverride Override(IncomeColumn column, IncomeLineCategory category, decimal monthly) => new("ov", column, category, monthly);

    private static CmiResult Compute(
        IReadOnlyList<Debtor>? debtors = null,
        IReadOnlyList<(string, EmploymentBody)>? employments = null,
        IReadOnlyList<PayPeriodRecordBody>? payPeriods = null,
        IReadOnlyList<OtherIncomeRecordBody>? otherIncome = null,
        IReadOnlyList<IncomeLineOverride>? overrides = null) =>
        Cmi.CurrentMonthlyIncome(
            new DateOnly(2026, 9, 15),
            debtors ?? [Debtor1],
            employments ?? [("em-1", Employment)],
            payPeriods ?? [],
            otherIncome ?? [],
            overrides ?? []);
}
