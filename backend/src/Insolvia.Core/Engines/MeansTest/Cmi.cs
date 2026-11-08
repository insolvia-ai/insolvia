using Insolvia.Core;
using Insolvia.Core.Debtors;
using Insolvia.Core.Income;
using Insolvia.Core.MeansTest;

namespace Insolvia.Engines.MeansTest;

// Current Monthly Income under § 101(10A) — the figure the means test keys off
// (from core/cmi.py, issue #100). Deterministic over the dated income
// history: receipt dates control, wages count gross, business and rental
// receipts net their expenses floored at zero, exclusions are shown not
// dropped, gaps are surfaced not zeroed, columns follow B122A-1, and an
// entered override replaces a line and says so (issue #349).
//
// Rounding: `_money` quantizes with the context default (half-even); the
// monthly averages (÷ 6) quantize half-up — both part of the contract.

/// <summary>The six-month lookback: the first day of the earliest month through the last day of the month before filing.</summary>
public sealed record CmiWindow(DateOnly FilingDate, DateOnly Start, DateOnly End, IReadOnlyList<string> Months)
{
    /// <summary>Whether <paramref name="when"/> falls inside the window.</summary>
    public bool Contains(DateOnly when) => Start <= when && when <= End;
}

/// <summary>One dated receipt that fed a line.</summary>
public sealed record CmiEntry(string ReceivedOn, string Amount, string Description);

/// <summary>One income line of one column, with its total, its monthly average and every entry behind them.</summary>
public sealed record CmiLine(
    string Category,
    string Label,
    string TotalReceived,
    string MonthlyAverage,
    IReadOnlyList<CmiEntry> Entries,
    string Citation = "",
    string Note = "",
    string? GrossMonthlyAverage = null,
    string? ExpensesMonthlyAverage = null);

/// <summary>Window months with no paycheck from an employment that looks like it should have produced one.</summary>
public sealed record CmiGap(string Employer, IReadOnlyList<string> Months);

/// <summary>One B122A-1 column: A is debtor 1, B debtor 2 or a non-filing spouse.</summary>
public sealed record CmiColumn(string Column, IReadOnlyList<CmiLine> Lines, IReadOnlyList<CmiLine> Excluded, string MonthlyTotal);

/// <summary>The whole derivation.</summary>
public sealed record CmiResult(
    CmiWindow Window,
    IReadOnlyList<CmiColumn> Columns,
    string CombinedMonthlyTotal,
    string Annualized,
    IReadOnlyList<CmiGap> Gaps,
    IReadOnlyList<string> Problems);

/// <summary>The CMI derivation (Python's <c>core/cmi.py</c>).</summary>
public static class Cmi
{
    /// <summary>The § 101(10A)(B)(ii) citation every excluded line carries.</summary>
    public const string ExclusionCitation = "11 U.S.C. § 101(10A)(B)(ii)";

    /// <summary>What an overridden line's note says.</summary>
    public const string OverrideNote =
        "entered manually (means_test_input.income_overrides) — replaces the "
        + "figure derived from the dated records";

    /// <summary>Line 8's contention category.</summary>
    public const string UnemploymentAsSsa = "unemployment_as_ssa";

    /// <summary>The § 101(10A)(B)(ii) categories, in the wire order.</summary>
    public static readonly IReadOnlyList<string> ExcludedCategories = [.. IncomeRules.ExcludedCategories.Select(category => category.ToWire())];

    internal static readonly IReadOnlyDictionary<string, string> IncomeLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["wages"] = "Gross wages, salary, tips, bonuses, overtime, commissions",
        ["alimony_maintenance"] = "Alimony and maintenance payments",
        ["household_contributions"] = "Amounts contributed by others to household expenses",
        ["business"] = "Net income from operating a business, profession, or farm",
        ["rental"] = "Net income from rental and other real property",
        ["interest_dividends_royalties"] = "Interest, dividends, and royalties",
        ["unemployment"] = "Unemployment compensation",
        ["pension_retirement"] = "Pension or retirement income",
        ["other"] = "Income from all other sources",
        ["social_security_act_benefit"] = "Benefits received under the Social Security Act",
        ["veterans_disability_compensation"] = "Veterans' disability or combat-related compensation (HAVEN Act)",
        ["war_crime_victim_payment"] = "Payments to victims of war crimes",
        ["terrorism_victim_payment"] = "Payments to victims of terrorism",
        [UnemploymentAsSsa] = "Unemployment compensation claimed as a Social Security Act benefit",
    };

    private static readonly string[] LineOrder =
    [
        "wages",
        "alimony_maintenance",
        "household_contributions",
        "business",
        "rental",
        "interest_dividends_royalties",
        "unemployment",
        "pension_retirement",
        "other",
    ];

    /// <summary>The § 101(10A)(A)(i) window for an (anticipated) filing date.</summary>
    public static CmiWindow Window(DateOnly filingDate)
    {
        var end = new DateOnly(filingDate.Year, filingDate.Month, 1).AddDays(-1);
        var months = new List<string>();
        var (year, month) = (end.Year, end.Month);
        for (var i = 0; i < 6; i++)
        {
            months.Add($"{year:D4}-{month:D2}");
            month--;
            if (month == 0)
            {
                (year, month) = (year - 1, 12);
            }
        }

        var earliest = months[^1];
        var start = new DateOnly(int.Parse(earliest[..4], System.Globalization.CultureInfo.InvariantCulture), int.Parse(earliest[5..], System.Globalization.CultureInfo.InvariantCulture), 1);
        months.Reverse();
        return new CmiWindow(filingDate, start, end, months);
    }

    /// <summary>
    /// The CMI derivation for a case, line by line. <paramref name="employments"/>
    /// are (id, body) pairs because pay periods reference them by id;
    /// <paramref name="overrides"/> are the entered lines, applied last.
    /// </summary>
    public static CmiResult CurrentMonthlyIncome(
        DateOnly filingDate,
        IReadOnlyList<Debtor> debtors,
        IReadOnlyList<(string Id, EmploymentBody Body)> employments,
        IReadOnlyList<PayPeriodRecordBody> payPeriods,
        IReadOnlyList<OtherIncomeRecordBody> otherIncome,
        IReadOnlyList<IncomeLineOverride>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(debtors);
        ArgumentNullException.ThrowIfNull(otherIncome);
        var window = Window(filingDate);
        var problems = new List<string>();
        var debtorsById = new Dictionary<string, Debtor>(StringComparer.Ordinal);
        foreach (var debtor in debtors)
        {
            debtorsById[debtor.Id] = debtor;
        }

        var (wageEntries, wageTotals, gaps) = WageLines(window, employments, payPeriods, debtorsById, problems);

        // column -> category -> (entries, gross total, expense total), insertion-ordered.
        var buckets = new Dictionary<string, Dictionary<string, (List<CmiEntry> Entries, decimal Gross, decimal Expenses)>>(StringComparer.Ordinal);
        foreach (var record in otherIncome)
        {
            var category = record.Category?.ToWire();
            var label = category ?? "uncategorised";
            if (record.ReceivedOn is not { } receivedOn)
            {
                problems.Add($"an {label} receipt has no received-on date — the lookback window cannot place it");
                continue;
            }

            var receivedText = Exact.Iso(receivedOn);
            if (category is null)
            {
                problems.Add($"the {receivedText} receipt has no category — say what kind of income it is");
                continue;
            }

            if (record.Amount is not { } amount)
            {
                problems.Add($"the {receivedText} {label} receipt has no amount");
                continue;
            }

            if (!window.Contains(receivedOn))
            {
                continue;
            }

            var column = ColumnFor(debtorsById.GetValueOrDefault(record.DebtorId ?? string.Empty));
            if (column is null)
            {
                problems.Add($"the {receivedText} {label} receipt names no debtor — say whose income this is");
                continue;
            }

            if (!buckets.TryGetValue(column, out var byCategory))
            {
                byCategory = new Dictionary<string, (List<CmiEntry>, decimal, decimal)>(StringComparer.Ordinal);
                buckets[column] = byCategory;
            }

            var (entries, gross, expenses) = byCategory.TryGetValue(category, out var existing) ? existing : ([], 0m, 0m);
            gross += amount;
            if (record.Expenses is { } recordExpenses)
            {
                expenses += recordExpenses;
            }

            entries.Add(new CmiEntry(
                receivedText,
                Exact.F(amount),
                IncomeLabels[category]
                    + (string.IsNullOrEmpty(record.Payer) ? string.Empty : $" — {record.Payer}")
                    + (record.Expenses is { } shown ? $" (expenses {Exact.F(shown)})" : string.Empty)));
            byCategory[category] = (entries, gross, expenses);
        }

        var columns = new List<CmiColumn>();
        var combined = 0m;
        foreach (var column in new[] { "A", "B" })
        {
            var lines = new List<CmiLine>();
            var excluded = new List<CmiLine>();
            var total = 0m;

            if (wageEntries.TryGetValue(column, out var wages))
            {
                var wageTotal = wageTotals[column];
                lines.Add(Line("wages", wages, wageTotal));
                total += wageTotal;
            }

            var columnBuckets = buckets.GetValueOrDefault(column);
            foreach (var category in LineOrder.Skip(1))
            {
                if (columnBuckets is null || !columnBuckets.TryGetValue(category, out var bucket))
                {
                    continue;
                }

                var (entries, gross, expenses) = bucket;
                if (category is "business" or "rental")
                {
                    var net = gross - expenses;
                    var note = $"gross receipts {Money(gross)} less ordinary and necessary operating expenses {Money(expenses)}";
                    if (net < 0)
                    {
                        note += "; a net loss enters as zero (B122A-1 lines 5-6)";
                        net = 0m;
                    }

                    var line = Line(category, entries, net, note: note);
                    lines.Add(line with
                    {
                        Citation = string.Empty,
                        GrossMonthlyAverage = MonthlyAverage(gross),
                        ExpensesMonthlyAverage = MonthlyAverage(expenses),
                    });
                    total += net;
                }
                else
                {
                    lines.Add(Line(category, entries, gross));
                    total += gross;
                }
            }

            foreach (var category in ExcludedCategories)
            {
                if (columnBuckets is null || !columnBuckets.TryGetValue(category, out var bucket))
                {
                    continue;
                }

                excluded.Add(Line(category, bucket.Entries, bucket.Gross, citation: ExclusionCitation, note: "recorded and excluded from current monthly income"));
            }

            (lines, excluded, total) = ApplyOverrides(column, lines, excluded, total, overrides ?? []);

            if (lines.Count > 0 || excluded.Count > 0)
            {
                columns.Add(new CmiColumn(column, lines, excluded, MonthlyAverage(total)));
                combined += total;
            }
        }

        var monthly = Exact.Quantize(combined / 6, Rounding.HalfUp);
        return new CmiResult(
            window,
            columns,
            Exact.F(monthly),
            Exact.F(Exact.Quantize(monthly * 12, Rounding.HalfEven)),
            gaps,
            problems);
    }

    internal static string? ColumnFor(Debtor? debtor) => debtor?.FilingRole switch
    {
        FilingRole.Debtor1 => "A",
        FilingRole.Debtor2 or FilingRole.NonFilingSpouse => "B",
        _ => null,
    };

    // `_money`: quantize with the context default, ROUND_HALF_EVEN.
    private static string Money(decimal value) => Exact.Money(value, Rounding.HalfEven);

    // `_monthly_average`: (total / 6) quantized ROUND_HALF_UP.
    private static string MonthlyAverage(decimal total) => Exact.Money(total / 6, Rounding.HalfUp);

    private static (Dictionary<string, List<CmiEntry>> Entries, Dictionary<string, decimal> Totals, List<CmiGap> Gaps) WageLines(
        CmiWindow window,
        IReadOnlyList<(string Id, EmploymentBody Body)> employments,
        IReadOnlyList<PayPeriodRecordBody> payPeriods,
        Dictionary<string, Debtor> debtorsById,
        List<string> problems)
    {
        var employmentsById = new Dictionary<string, EmploymentBody>(StringComparer.Ordinal);
        var paidMonths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (id, body) in employments)
        {
            employmentsById[id] = body;
            paidMonths[id] = new HashSet<string>(StringComparer.Ordinal);
        }

        var entries = new Dictionary<string, List<CmiEntry>>(StringComparer.Ordinal);
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var record in payPeriods)
        {
            var where = $"pay period paid {(record.PayDate is { } paid ? Exact.Iso(paid) : "undated")}";
            var employment = record.EmploymentId is not null ? employmentsById.GetValueOrDefault(record.EmploymentId) : null;
            if (employment is null)
            {
                problems.Add($"{where}: names no existing employment — attach it to the employer it came from");
                continue;
            }

            var employer = string.IsNullOrEmpty(employment.EmployerName) ? "unnamed employer" : employment.EmployerName;
            if (record.PayDate is not { } payDate)
            {
                problems.Add($"a pay period from {employer} has no pay date — the lookback window cannot place it");
                continue;
            }

            if (!window.Contains(payDate))
            {
                continue;
            }

            var payText = Exact.Iso(payDate);
            if (record.Gross is not { } gross)
            {
                problems.Add($"the {payText} paycheck from {employer} has no gross amount — CMI counts gross wages");
                continue;
            }

            var column = ColumnFor(debtorsById.GetValueOrDefault(employment.DebtorId ?? string.Empty));
            if (column is null)
            {
                problems.Add($"the employment at {employer} names no debtor — say whose income this is");
                continue;
            }

            paidMonths[record.EmploymentId!].Add(payText[..7]);
            totals[column] = totals.GetValueOrDefault(column) + gross;
            if (!entries.TryGetValue(column, out var list))
            {
                list = [];
                entries[column] = list;
            }

            list.Add(new CmiEntry(payText, Exact.F(gross), $"paycheck (gross) — {employer}"));
        }

        var gaps = new List<CmiGap>();
        foreach (var (id, employment) in employments)
        {
            var monthsPaid = paidMonths.GetValueOrDefault(id) ?? [];
            var active = employment.Status == EmploymentStatus.Employed || monthsPaid.Count > 0;
            if (!active)
            {
                continue;
            }

            IEnumerable<string> expected = window.Months;
            if (employment.EmployedSince is { } since)
            {
                var hiredMonth = Exact.Iso(since)[..7];
                expected = expected.Where(month => string.CompareOrdinal(month, hiredMonth) >= 0);
            }

            var missing = expected.Where(month => !monthsPaid.Contains(month)).ToList();
            if (missing.Count > 0)
            {
                gaps.Add(new CmiGap(string.IsNullOrEmpty(employment.EmployerName) ? "unnamed employer" : employment.EmployerName, missing));
            }
        }

        return (entries, totals, gaps);
    }

    private static CmiLine Line(string category, List<CmiEntry> entries, decimal total, string citation = "", string note = "")
    {
        var ordered = entries
            .OrderBy(entry => entry.ReceivedOn, StringComparer.Ordinal)
            .ThenBy(entry => entry.Description, StringComparer.Ordinal)
            .ToList();
        return new CmiLine(category, IncomeLabels[category], Money(total), MonthlyAverage(total), ordered, citation, note);
    }

    private static CmiLine OverrideLine(string category, decimal monthly, bool excluded) =>
        new(category, IncomeLabels[category], Money(monthly * 6), Money(monthly), [], excluded ? ExclusionCitation : string.Empty, OverrideNote);

    private static (List<CmiLine> Lines, List<CmiLine> Excluded, decimal Total) ApplyOverrides(
        string column,
        List<CmiLine> lines,
        List<CmiLine> excluded,
        decimal total,
        IReadOnlyList<IncomeLineOverride> overrides)
    {
        foreach (var entered in overrides)
        {
            if (entered.Column?.ToWire() != column || entered.Category is null || entered.MonthlyAmount is not { } monthly)
            {
                continue;
            }

            var category = entered.Category.Value.ToWire();
            if (category == UnemploymentAsSsa)
            {
                var existing = lines.FirstOrDefault(line => line.Category == "unemployment");
                if (existing is not null)
                {
                    var before = Exact.Dec(existing.TotalReceived);
                    var after = Exact.Max(before - (monthly * 6), 0m);
                    total -= before - after;
                    lines[lines.IndexOf(existing)] = existing with
                    {
                        TotalReceived = Money(after),
                        MonthlyAverage = MonthlyAverage(after),
                        Note = $"{Money(monthly)} per month is contended to be a Social Security Act benefit and listed as excluded (B122A-1 line 8)",
                        GrossMonthlyAverage = null,
                        ExpensesMonthlyAverage = null,
                    };
                }

                excluded = [.. excluded.Where(line => line.Category != category)];
                excluded.Add(OverrideLine(category, monthly, excluded: true));
            }
            else if (ExcludedCategories.Contains(category))
            {
                excluded = [.. excluded.Where(line => line.Category != category)];
                excluded.Add(OverrideLine(category, monthly, excluded: true));
            }
            else
            {
                var existing = lines.FirstOrDefault(line => line.Category == category);
                if (existing is not null)
                {
                    total -= Exact.Dec(existing.TotalReceived);
                    lines = [.. lines.Where(line => line.Category != category)];
                }

                total += monthly * 6;
                lines.Add(OverrideLine(category, monthly, excluded: false));
            }
        }

        lines = [.. lines.OrderBy(line => Array.IndexOf(LineOrder, line.Category))];
        return (lines, excluded, total);
    }
}
