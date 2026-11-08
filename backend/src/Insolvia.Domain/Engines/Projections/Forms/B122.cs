using Insolvia.Core;
using Insolvia.Core.Claims;
using Insolvia.Core.Debtors;
using Insolvia.Core.MeansTest;
using Insolvia.Engines.MeansTest;
using Insolvia.Engines.Regulatory;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B122A-1 @ 2019-12-01 — the Chapter 7 statement of current monthly income
/// (from <c>form_projections/b122a1.py</c>), and the helpers the other
/// means-test forms, the trace and the plan share: the as-of date, the
/// entered inputs, the CMI, the household size and the marital status.
/// </summary>
public static class B122A1
{
    private static readonly Dictionary<string, string> CategoryFields = new(StringComparer.Ordinal)
    {
        ["wages"] = "wages",
        ["alimony_maintenance"] = "alimony",
        ["household_contributions"] = "household_contributions",
        ["interest_dividends_royalties"] = "interest_dividends",
        ["unemployment"] = "unemployment",
        ["pension_retirement"] = "pension",
        ["other"] = "other_income_1",
    };

    private static readonly string[] ZeroFilled =
    [
        "wages",
        "alimony",
        "household_contributions",
        "business_gross",
        "business_expenses",
        "business_net",
        "rental_gross",
        "rental_expenses",
        "rental_net",
        "interest_dividends",
        "unemployment",
        "pension",
        "other_income_1",
        "other_income_2",
        "column_total",
    ];

    /// <summary>The registry-resolution and lookback date, and where it came from.</summary>
    public static (DateOnly AsOf, string Source) MeansTestAsOf(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        return caseFile.Petition?.ExpectedFilingDate is { } expected
            ? (expected, "petition.expected_filing_date")
            : (caseFile.CreatedOn, "case.created_at");
    }

    /// <summary>The case's entered figures — the first record, or an empty body.</summary>
    public static MeansTestInputBody MeansTestInputs(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        return caseFile.MeansTestInputs.Count > 0 ? caseFile.MeansTestInputs[0] : EmptyInputs;
    }

    /// <summary>An empty means-test input body (Python's <c>MeansTestInputBody()</c>).</summary>
    public static MeansTestInputBody EmptyInputs { get; } = MeansTestInputRules.Parse([]);

    /// <summary>The § 101(10A) derivation for this case file.</summary>
    public static CmiResult ComputeCmi(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        return Cmi.CurrentMonthlyIncome(
            MeansTestAsOf(caseFile).AsOf,
            caseFile.Debtors,
            caseFile.Employments,
            caseFile.PayPeriodRecords,
            caseFile.OtherIncomeRecords,
            MeansTestInputs(caseFile).IncomeOverrides);
    }

    /// <summary>Line 13's household size: the entered composition, or null.</summary>
    public static int? HouseholdSize(CaseFile caseFile) => MeansTestEngine.MedianHouseholdSize(MeansTestInputs(caseFile))?.Size;

    /// <summary>Line 1's answer and its source; null with the reason when the records contradict each other.</summary>
    public static (string? Status, string Source) MaritalFilingStatus(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        if (MeansTestInputs(caseFile).MaritalFilingStatus is { } entered)
        {
            return (entered.ToWire(), "entered (means_test_input.marital_filing_status)");
        }

        var debtor2 = caseFile.Debtor(FilingRole.Debtor2);
        var spouse = caseFile.Debtor(FilingRole.NonFilingSpouse);
        if (debtor2 is not null && spouse is not null)
        {
            return (null, "the case has both a debtor_2 and a non_filing_spouse record — line 1 cannot answer both 'filing with you' and 'not filing'");
        }

        if (debtor2 is not null)
        {
            return ("married_filing_jointly", "the case's debtor_2 record");
        }

        if (spouse is not null)
        {
            return ("married_not_filing_same_household", "the case's non_filing_spouse record");
        }

        return ("not_married", "the case's debtor records (no spouse recorded)");
    }

    /// <summary>One CMI column's lines 2-11 on the spec's per-column fields — shared with B122C-1.</summary>
    public static void FillCmiColumn(FormRelease release, FieldValues values, CmiColumn column, int index, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(column);
        var filled = new HashSet<string>(StringComparer.Ordinal);
        void Put(string fieldId, string amount)
        {
            filled.Add(fieldId);
            RowFill(release, values, fieldId, index, new TextFill(FormatMoney(Exact.Dec(amount))), problems);
        }

        foreach (var line in column.Lines)
        {
            if (line.Category is "business" or "rental")
            {
                Put($"{line.Category}_gross", line.GrossMonthlyAverage!);
                Put($"{line.Category}_expenses", line.ExpensesMonthlyAverage!);
                Put($"{line.Category}_net", line.MonthlyAverage);
            }
            else
            {
                Put(CategoryFields[line.Category], line.MonthlyAverage);
                if (line.Category == "other")
                {
                    var payers = line.Entries
                        .Where(entry => entry.Description.Contains(" — ", StringComparison.Ordinal))
                        .Select(entry => entry.Description.Split(" — ")[^1])
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToList();
                    var source = payers.Count > 0 ? string.Join("; ", payers) : "Other income";
                    RowFill(release, values, "other_income_source_1", 0, new TextFill(source), problems);
                }
            }
        }

        foreach (var line in column.Excluded)
        {
            if (line.Category == "social_security_act_benefit")
            {
                Put("ssa_contention", line.MonthlyAverage);
            }
        }

        Put("column_total", column.MonthlyTotal);
        foreach (var fieldId in ZeroFilled)
        {
            if (!filled.Contains(fieldId) && fieldId != "other_income_2")
            {
                RowFill(release, values, fieldId, index, new TextFill("0.00"), problems);
            }
        }
    }

    /// <summary>The two signature dates — shared with the Chapter 13 pair.</summary>
    public static void FillSignatureDates(FieldValues values, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(caseFile);
        foreach (var (role, fieldId) in new[] { (FilingRole.Debtor1, "debtor1_signature_date"), (FilingRole.Debtor2, "debtor2_signature_date") })
        {
            if (caseFile.Debtor(role)?.Body.SignedAt is { } signed)
            {
                values[fieldId] = new TextFill(FormatDate(signed));
            }
        }
    }

    /// <summary>The two names and the district — shared with the Chapter 13 pair.</summary>
    public static void FillCaption(FieldValues values, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(caseFile);
        if (FullName(caseFile.Debtor(FilingRole.Debtor1)) is { } name1)
        {
            values["caption.debtor1_name"] = new TextFill(name1);
        }

        if (FullName(caseFile.Debtor(FilingRole.Debtor2)) is { } name2)
        {
            values["caption.debtor2_name"] = new TextFill(name2);
        }

        if (!string.IsNullOrEmpty(caseFile.Case.District))
        {
            values["caption.district"] = new TextFill(caseFile.Case.District);
        }
    }

    /// <summary>Debtor 1's residence state, or null.</summary>
    internal static string? State(CaseFile caseFile) =>
        caseFile.Debtor(FilingRole.Debtor1)?.Body.ResidenceAddress.State is { Length: > 0 } state ? state : null;

    /// <summary>The values for <c>form/b122a1@2019-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var values = new FieldValues();
        var problems = new List<string>();

        FillCaption(values, caseFile);
        MaritalStatus(caseFile, values, problems);

        var cmi = ComputeCmi(caseFile);
        problems.AddRange(cmi.Problems);
        var columns = cmi.Columns.ToDictionary(column => column.Column, StringComparer.Ordinal);
        foreach (var (key, index) in new[] { ("A", 0), ("B", 1) })
        {
            if (columns.TryGetValue(key, out var column))
            {
                FillCmiColumn(release, values, column, index, problems);
            }
        }

        if (cmi.Columns.Count > 0)
        {
            values["total_cmi"] = new TextFill(FormatMoney(Exact.Dec(cmi.CombinedMonthlyTotal)));
            Determination(values, caseFile, cmi, problems);
        }

        FillSignatureDates(values, caseFile);

        if (problems.Count > 0)
        {
            throw new FormProjectionException(problems);
        }

        return values;
    }

    private static void MaritalStatus(CaseFile caseFile, FieldValues values, List<string> problems)
    {
        var (status, source) = MaritalFilingStatus(caseFile);
        switch (status)
        {
            case null:
                problems.Add(source);
                return;
            case "married_filing_jointly":
                // The PDF's own misspelled export state.
                values["marital_filing_status"] = new OptionFill("Maried and filing");
                break;
            case "married_not_filing_same_household":
                values["marital_filing_status"] = new OptionFill("Married but not filing");
                values["married_not_filing_household"] = new OptionFill("together");
                break;
            case "married_not_filing_separated":
                values["marital_filing_status"] = new OptionFill("Married but not filing");
                values["married_not_filing_household"] = new OptionFill("separately");
                break;
            default:
                values["marital_filing_status"] = new OptionFill("Not married");
                break;
        }
    }

    private static void Determination(FieldValues values, CaseFile caseFile, CmiResult cmi, List<string> problems)
    {
        values["annualized_cmi"] = new TextFill(FormatMoney(Exact.Dec(cmi.Annualized)));

        if (MeansTestEngine.PresumptionExemption(MeansTestInputs(caseFile)) is { } exemption)
        {
            values["caption.presumption_box"] = new OptionFill(exemption.Flag == "reservist_national_guard" ? "Does not apply" : "No Abuse");
            return;
        }

        var state = State(caseFile);
        var size = HouseholdSize(caseFile);
        if (state is not null)
        {
            values["median_state"] = new TextFill(state.ToUpperInvariant());
        }

        if (size is { } household)
        {
            values["median_household_size"] = new TextFill(household.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (state is null || size is null)
        {
            return;
        }

        var (_, table) = UstData.MedianIncomeTable(MeansTestAsOf(caseFile).AsOf);
        decimal median;
        try
        {
            median = table.AnnualMedian(state, size.Value);
        }
        catch (Exception error) when (error is EngineKeyException or EngineValueException)
        {
            problems.Add(Exact.Str(error));
            return;
        }

        values["median_income"] = new TextFill(FormatMoney(median));
        if (Exact.Dec(cmi.CombinedMonthlyTotal) * 12 > median)
        {
            values["median_comparison"] = new OptionFill("12b more than 13");
            values["caption.presumption_box"] = new OptionFill("Presumption of abuse applies");
        }
        else
        {
            values["median_comparison"] = new OptionFill("12b less or equal to 13");
            values["caption.presumption_box"] = new OptionFill("No Abuse");
        }
    }
}

/// <summary>
/// B122A-2 @ 2025-04-01 — the Chapter 7 means-test calculation (from
/// <c>form_projections/b122a2.py</c>): the engine's lines landed on the
/// spec's fields, the entered rows, and the determination's checkboxes.
/// </summary>
public static class B122A2
{
    private static readonly Dictionary<string, string> LineFields = new(StringComparer.Ordinal)
    {
        ["1"] = "total_cmi",
        ["3"] = "marital_adjustment_total",
        ["4"] = "adjusted_cmi",
        ["6"] = "food_clothing_allowance",
        ["7a"] = "oop_health_under_65_rate",
        ["7c"] = "oop_health_under_65_subtotal",
        ["7d"] = "oop_health_over_65_rate",
        ["7f"] = "oop_health_over_65_subtotal",
        ["7"] = "oop_health_total",
        ["8"] = "housing_operating",
        ["9a"] = "housing_mortgage_rent_standard",
        ["9b"] = "home_debt_total",
        ["9c"] = "net_mortgage_rent",
        ["10"] = "housing_adjustment",
        ["12"] = "vehicle_operating",
        ["13a"] = "vehicle_1_ownership_standard",
        ["13b"] = "vehicle_1_loan_total",
        ["13c"] = "vehicle_1_net",
        ["13d"] = "vehicle_2_ownership_standard",
        ["13e"] = "vehicle_2_loan_total",
        ["13f"] = "vehicle_2_net",
        ["14"] = "public_transportation",
        ["15"] = "additional_public_transportation",
        ["16"] = "taxes",
        ["17"] = "involuntary_deductions",
        ["18"] = "term_life_insurance",
        ["19"] = "court_ordered_payments",
        ["20"] = "education_for_employment",
        ["21"] = "childcare",
        ["22"] = "additional_health_care",
        ["23"] = "optional_telecom",
        ["24"] = "irs_allowances_total",
        ["26"] = "family_care",
        ["27"] = "family_violence",
        ["28"] = "home_energy",
        ["29"] = "education_under_18",
        ["30"] = "additional_food_clothing",
        ["31"] = "charitable_contributions",
        ["32"] = "additional_deductions_total",
        ["33e"] = "secured_debt_total",
        ["34"] = "cure_monthly_total",
        ["35"] = "priority_claims_monthly",
        ["36"] = "ch13_admin_expense",
        ["37"] = "debt_payment_total",
        ["38"] = "total_deductions",
        ["39c"] = "monthly_disposable_income",
        ["39d"] = "disposable_income_60_months",
        ["41a"] = "nonpriority_unsecured_total",
        ["41b"] = "nonpriority_unsecured_quarter",
    };

    private static readonly HashSet<string> UnprintedLines = new(StringComparer.Ordinal)
    {
        "5", "7b", "7e", "11", "13", "25", "33a", "33b", "33c", "33d", "39a", "39b", "40", "42",
    };

    /// <summary>Whether this case files Form 122A-2 — false only when the comparison determinately answers below the median or an exemption ends the test.</summary>
    public static bool Files(CaseFile caseFile)
    {
        if (MeansTestEngine.PresumptionExemption(B122A1.MeansTestInputs(caseFile)) is not null)
        {
            return false;
        }

        var size = B122A1.HouseholdSize(caseFile);
        var state = B122A1.State(caseFile);
        if (size is null || state is null)
        {
            return true;
        }

        decimal median;
        try
        {
            var (_, table) = UstData.MedianIncomeTable(B122A1.MeansTestAsOf(caseFile).AsOf);
            median = table.AnnualMedian(state, size.Value);
        }
        catch (Exception error) when (error is EngineLookupException or EngineValueException)
        {
            return true;
        }

        return Exact.Dec(B122A1.ComputeCmi(caseFile).CombinedMonthlyTotal) * 12 > median;
    }

    /// <summary>The engine's inputs, derived from the case records.</summary>
    public static MeansTestCase BuildMeansTestCase(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        var address = caseFile.Debtor(FilingRole.Debtor1)?.Body.ResidenceAddress;
        var claims = caseFile.Claims.Select(pair => pair.Body).ToList();
        var priority = claims.Where(body => body.ClaimClass == ClaimClass.PriorityUnsecured).Aggregate(0m, (sum, body) => sum + Amount(body.PriorityAmount));
        var nonpriority = claims.Where(body => body.ClaimClass == ClaimClass.NonpriorityUnsecured).Aggregate(0m, (sum, body) => sum + Amount(body.Amount))
            + claims.Where(body => body.ClaimClass == ClaimClass.PriorityUnsecured).Aggregate(0m, (sum, body) => sum + Amount(body.NonpriorityAmount));
        return new MeansTestCase(
            State: string.IsNullOrEmpty(address?.State) ? string.Empty : address.State,
            County: string.IsNullOrEmpty(address?.County) ? string.Empty : address.County,
            District: caseFile.Case.District,
            Cmi: B122A1.ComputeCmi(caseFile),
            Inputs: B122A1.MeansTestInputs(caseFile),
            PriorityDebtTotal: Exact.F(priority),
            NonpriorityUnsecuredTotal: Exact.F(nonpriority),
            ChildrenUnder18: caseFile.Dependents.Count(dependent => dependent.Age is < 18),
            Chapter: caseFile.Case.Chapter);
    }

    /// <summary>The values for <c>form/b122a2@2025-04-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        if (caseFile.Case.Chapter != 7)
        {
            throw new FormProjectionException([$"Form 122A-2 is the Chapter 7 means test — this is a Chapter {caseFile.Case.Chapter} case"]);
        }

        var test = BuildMeansTestCase(caseFile);
        problems.AddRange(test.Cmi.Problems);
        if (problems.Count > 0)
        {
            throw new FormProjectionException(problems);
        }

        MeansTestData data;
        MeansTestResult result;
        try
        {
            data = MeansTestEngine.ResolveData(B122A1.MeansTestAsOf(caseFile).AsOf);
            result = MeansTestEngine.Run(test, data);
        }
        catch (MeansTestException error)
        {
            throw new FormProjectionException(error.Problems);
        }
        catch (EngineLookupException error)
        {
            throw new FormProjectionException([Exact.Str(error)]);
        }

        if (result.Outcome == "below_median")
        {
            throw new FormProjectionException(["the debtor's annualized income is below the applicable median — Form 122A-2 is not filed (B122A-1 line 14a)"]);
        }

        if (result.Outcome == "exempt")
        {
            throw new FormProjectionException([$"the debtor is exempt from the presumption ({result.DeterminedBy}) — Form 122A-2 is not filed (Form 122A-1Supp)"]);
        }

        var values = new FieldValues();
        CaptionAndSignatures(values, caseFile);

        var byLine = new OrderedDictionary<string, MeansTestLine>(StringComparer.Ordinal);
        foreach (var line in result.Lines)
        {
            byLine[line.Line] = line;
        }

        foreach (var (number, line) in byLine)
        {
            if (!LineFields.TryGetValue(number, out var fieldId))
            {
                if (!UnprintedLines.Contains(number))
                {
                    throw new FormProjectionException([$"engine line {number} has no field mapping"]);
                }

                continue;
            }

            values[fieldId] = new TextFill(FormatMoney(Exact.Dec(line.Amount)));
        }

        values["household_size"] = new TextFill(byLine["5"].Amount.Split('.')[0]);
        values["oop_health_under_65_count"] = new TextFill(byLine["7b"].Amount.Split('.')[0]);
        values["oop_health_over_65_count"] = new TextFill(byLine["7e"].Amount.Split('.')[0]);

        if (Exact.Dec(test.PriorityDebtTotal) > 0)
        {
            values["priority_claims_total"] = new TextFill(FormatMoney(Exact.Dec(test.PriorityDebtTotal)));
        }

        if (test.Inputs.Ch13Eligible == true)
        {
            values["ch13_multiplier"] = new TextFill(Exact.F(data.Ch13.MultiplierFor(test.District)));
        }

        EnteredRows(release, values, caseFile, problems);
        Checkboxes(values, caseFile, test, result);

        if (problems.Count > 0)
        {
            throw new FormProjectionException(problems);
        }

        return values;
    }

    private static void EnteredRows(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        if (caseFile.MeansTestInputs.Count == 0)
        {
            return;
        }

        var inputs = caseFile.MeansTestInputs[0];
        for (var index = 0; index < inputs.MaritalAdjustments.Count; index++)
        {
            var item = inputs.MaritalAdjustments[index];
            RowFill(release, values, "marital_adjustment_purpose", index, TextOrNone(item.Description), problems);
            RowFill(release, values, "marital_adjustment_amount", index, item.Amount is { } amount ? new TextFill(FormatMoney(amount)) : null, problems);
        }

        for (var index = 0; index < inputs.OtherSecuredPayments.Count; index++)
        {
            var payment = inputs.OtherSecuredPayments[index];
            RowFill(release, values, "other_secured_creditor", index, TextOrNone(payment.CreditorName), problems);
            RowFill(release, values, "other_secured_property", index, TextOrNone(payment.PropertyDescription), problems);
            RowFill(release, values, "other_secured_monthly", index, payment.MonthlyPayment is { } monthly ? new TextFill(FormatMoney(monthly)) : null, problems);
        }

        if (!string.IsNullOrEmpty(inputs.HousingAdjustmentExplanation))
        {
            RowFill(release, values, "housing_adjustment_explanation", 0, new TextFill(inputs.HousingAdjustmentExplanation), problems);
        }
    }

    private static void Checkboxes(FieldValues values, CaseFile caseFile, MeansTestCase test, MeansTestResult result)
    {
        var inputs = test.Inputs;
        var hasColumnB = test.Cmi.Columns.Any(column => column.Column == "B");
        values["column_b_filled"] = new OptionFill(hasColumnB ? "Yes" : "No");
        if (hasColumnB)
        {
            var spouseFiling = caseFile.Debtor(FilingRole.Debtor2) is not null;
            values["spouse_filing"] = new OptionFill(spouseFiling ? "Yes" : "No");
            if (!spouseFiling)
            {
                values["marital_adjustment_claimed"] = new OptionFill(inputs.MaritalAdjustments.Count > 0 ? "On" : "No");
            }
        }

        if (inputs.VehicleCount is { } vehicles)
        {
            values["vehicle_count"] = new OptionFill(vehicles switch
            {
                0 => "0",
                1 => "1",
                2 => "On",
                _ => throw new EngineKeyException(vehicles.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            });
        }

        values["cure_claimed"] = new OptionFill(inputs.PriorityCureTotal is not null ? "Yes" : "No");
        values["priority_claims_owed"] = new OptionFill(Exact.Dec(test.PriorityDebtTotal) > 0 ? "Yes" : "No");
        if (inputs.Ch13Eligible is { } eligible)
        {
            values["ch13_eligible"] = new OptionFill(eligible ? "Yes" : "No");
        }

        values["presumption_thresholds"] = new OptionFill(result.DeterminedBy switch
        {
            "threshold_floor" => "1",
            "threshold_ceiling" => "2",
            "unsecured_ratio" => "On",
            _ => throw new EngineKeyException(result.DeterminedBy),
        });
        if (result.DeterminedBy == "unsecured_ratio")
        {
            values["unsecured_ratio_determination"] = new OptionFill(result.Outcome == "presumption_of_abuse" ? "Yes" : "No");
        }

        values["caption.presumption_box"] = new OptionFill(result.Outcome == "presumption_of_abuse" ? "Presumption of abuse applies" : "No Abuse");
    }

    private static void CaptionAndSignatures(FieldValues values, CaseFile caseFile)
    {
        B122A1.FillCaption(values, caseFile);
        B122A1.FillSignatureDates(values, caseFile);
    }
}

/// <summary>
/// B122C-1 @ 2019-10-01 — the Chapter 13 statement of current monthly income
/// and commitment period (from <c>form_projections/b122c1.py</c>).
/// </summary>
public static class B122C1
{
    private static readonly Dictionary<string, string> LineFields = new(StringComparer.Ordinal)
    {
        ["13"] = "marital_adjustment_total",
        ["14"] = "current_monthly_income",
        ["15b"] = "annualized_cmi",
        ["16c"] = "median_income",
        ["19a"] = "commitment_marital_adjustment",
        ["19b"] = "commitment_monthly_income",
        ["20b"] = "commitment_annualized",
    };

    /// <summary>The values for <c>form/b122c1@2019-10-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        if (caseFile.Case.Chapter != 13)
        {
            throw new FormProjectionException([$"Form 122C-1 is the Chapter 13 statement of current monthly income — this is a Chapter {caseFile.Case.Chapter} case"]);
        }

        var values = new FieldValues();
        var problems = new List<string>();
        B122A1.FillCaption(values, caseFile);
        var status = MaritalStatus(caseFile, values, problems);

        var cmi = B122A1.ComputeCmi(caseFile);
        problems.AddRange(cmi.Problems);
        var columns = cmi.Columns.ToDictionary(column => column.Column, StringComparer.Ordinal);
        foreach (var (key, index) in new[] { ("A", 0), ("B", 1) })
        {
            if (columns.TryGetValue(key, out var column))
            {
                B122A1.FillCmiColumn(release, values, column, index, problems);
            }
        }

        if (cmi.Columns.Count > 0)
        {
            values["total_cmi"] = new TextFill(FormatMoney(Exact.Dec(cmi.CombinedMonthlyTotal)));
            Determination(values, caseFile, status, problems);
        }

        var adjustments = B122A1.MeansTestInputs(caseFile).MaritalAdjustments;
        for (var index = 0; index < adjustments.Count; index++)
        {
            var item = adjustments[index];
            RowFill(release, values, "marital_adjustment_purpose", index, TextOrNone(item.Description), problems);
            RowFill(release, values, "marital_adjustment_amount", index, item.Amount is { } amount ? new TextFill(FormatMoney(amount)) : null, problems);
        }

        B122A1.FillSignatureDates(values, caseFile);

        if (problems.Count > 0)
        {
            throw new FormProjectionException(problems);
        }

        return values;
    }

    private static string? MaritalStatus(CaseFile caseFile, FieldValues values, List<string> problems)
    {
        var (status, source) = B122A1.MaritalFilingStatus(caseFile);
        if (status is null)
        {
            problems.Add(source);
            return null;
        }

        values["marital_filing_status"] = new OptionFill(status == "not_married" ? "single" : "married");
        return status;
    }

    private static void Determination(FieldValues values, CaseFile caseFile, string? status, List<string> problems)
    {
        var state = B122A1.State(caseFile);
        var size = B122A1.HouseholdSize(caseFile);
        if (state is not null)
        {
            values["median_state"] = new TextFill(state.ToUpperInvariant());
        }

        if (size is { } household)
        {
            values["median_household_size"] = new TextFill(household.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (status is not null)
        {
            values["marital_adjustment_status"] = new OptionFill(status switch
            {
                "not_married" => "1",
                "married_filing_jointly" => "2",
                _ => "3",
            });
        }

        if (state is null || size is null)
        {
            return;
        }

        MeansTestResult result;
        try
        {
            var data = MeansTestEngine.ResolveData(B122A1.MeansTestAsOf(caseFile).AsOf);
            result = MeansTestEngine.Run(B122A2.BuildMeansTestCase(caseFile), data);
        }
        catch (MeansTestException error)
        {
            problems.AddRange(error.Problems);
            return;
        }
        catch (EngineLookupException error)
        {
            problems.Add(Exact.Str(error));
            return;
        }

        foreach (var line in result.Lines)
        {
            if (line.Form == MeansTestEngine.Form122C1 && LineFields.TryGetValue(line.Line, out var fieldId))
            {
                values[fieldId] = new TextFill(FormatMoney(Exact.Dec(line.Amount)));
            }
        }

        if (result.Comparison!.AboveMedian)
        {
            values["median_comparison"] = new OptionFill("17b");
            values["caption.disposable_income_box"] = new OptionFill("2");
        }
        else
        {
            values["median_comparison"] = new OptionFill("17a");
            values["caption.disposable_income_box"] = new OptionFill("1");
        }

        if (result.Commitment!.Months == MeansTestEngine.CommitmentPeriodBelowMedian)
        {
            values["commitment_comparison"] = new OptionFill("yes");
            values["caption.commitment_period_box"] = new OptionFill("3");
        }
        else
        {
            values["commitment_comparison"] = new OptionFill("no");
            values["caption.commitment_period_box"] = new OptionFill("4");
        }
    }
}

/// <summary>
/// B122C-2 @ 2025-04-01 — the Chapter 13 disposable-income calculation (from
/// <c>form_projections/b122c2.py</c>).
/// </summary>
public static class B122C2
{
    private static readonly Dictionary<string, string> LineFields = new(StringComparer.Ordinal)
    {
        ["6"] = "food_clothing_allowance",
        ["7a"] = "oop_health_under_65_rate",
        ["7c"] = "oop_health_under_65_subtotal",
        ["7d"] = "oop_health_over_65_rate",
        ["7f"] = "oop_health_over_65_subtotal",
        ["7"] = "oop_health_total",
        ["8"] = "housing_operating",
        ["9a"] = "housing_mortgage_rent_standard",
        ["9b"] = "home_debt_total",
        ["9c"] = "net_mortgage_rent",
        ["10"] = "housing_adjustment",
        ["12"] = "vehicle_operating",
        ["13a"] = "vehicle_1_ownership_standard",
        ["13b"] = "vehicle_1_loan_total",
        ["13c"] = "vehicle_1_net",
        ["13d"] = "vehicle_2_ownership_standard",
        ["13e"] = "vehicle_2_loan_total",
        ["13f"] = "vehicle_2_net",
        ["14"] = "public_transportation",
        ["15"] = "additional_public_transportation",
        ["16"] = "taxes",
        ["17"] = "involuntary_deductions",
        ["18"] = "term_life_insurance",
        ["19"] = "court_ordered_payments",
        ["20"] = "education_for_employment",
        ["21"] = "childcare",
        ["22"] = "additional_health_care",
        ["23"] = "optional_telecom",
        ["24"] = "irs_allowances_total",
        ["25"] = "health_insurance_total",
        ["26"] = "family_care",
        ["27"] = "family_violence",
        ["28"] = "home_energy",
        ["29"] = "education_under_18",
        ["30"] = "additional_food_clothing",
        ["31"] = "charitable_contributions",
        ["32"] = "additional_deductions_total",
        ["33e"] = "secured_debt_total",
        ["34"] = "cure_monthly_total",
        ["35"] = "priority_claims_monthly",
        ["36"] = "ch13_admin_expense",
        ["37"] = "debt_payment_total",
        ["38"] = "total_deductions",
        ["39"] = "current_monthly_income",
        ["40"] = "child_support_income",
        ["41"] = "qualified_retirement_deductions",
        ["43"] = "special_circumstances_total",
        ["44"] = "total_adjustments",
        ["45"] = "monthly_disposable_income",
    };

    private static readonly HashSet<string> UnprintedLines = new(StringComparer.Ordinal) { "5", "7b", "7e", "11", "13", "33a", "33b", "33c", "33d", "42" };

    /// <summary>Whether this Chapter 13 case files Form 122C-2 — false only when the engine determinately answers below the median.</summary>
    public static bool Files(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        if (caseFile.Case.Chapter != 13)
        {
            return false;
        }

        try
        {
            return Run(caseFile).Result.Outcome != "below_median";
        }
        catch (FormProjectionException)
        {
            return true;
        }
    }

    /// <summary>The values for <c>form/b122c2@2025-04-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        if (caseFile.Case.Chapter != 13)
        {
            throw new FormProjectionException([$"Form 122C-2 is the Chapter 13 disposable-income calculation — this is a Chapter {caseFile.Case.Chapter} case"]);
        }

        var problems = new List<string>();
        var (test, result, data) = Run(caseFile);
        if (result.Outcome == "below_median")
        {
            throw new FormProjectionException(["the debtor's current monthly income is at or below the applicable median — Form 122C-2 is not filed (B122C-1 line 17a)"]);
        }

        var values = new FieldValues();
        B122A1.FillCaption(values, caseFile);

        var byLine = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.Lines.Where(line => line.Form == MeansTestEngine.Form122C2))
        {
            byLine[line.Line] = line.Amount;
        }

        foreach (var (number, printed) in byLine)
        {
            if (!LineFields.TryGetValue(number, out var fieldId))
            {
                if (!UnprintedLines.Contains(number))
                {
                    throw new FormProjectionException([$"engine line {number} has no field mapping"]);
                }

                continue;
            }

            values[fieldId] = new TextFill(FormatMoney(Exact.Dec(printed)));
        }

        values["household_size"] = new TextFill(byLine["5"].Split('.')[0]);
        values["oop_health_under_65_count"] = new TextFill(byLine["7b"].Split('.')[0]);
        values["oop_health_over_65_count"] = new TextFill(byLine["7e"].Split('.')[0]);

        if (Exact.Dec(test.PriorityDebtTotal) > 0)
        {
            values["priority_claims_total"] = new TextFill(FormatMoney(Exact.Dec(test.PriorityDebtTotal)));
        }

        values["ch13_multiplier"] = new TextFill(Exact.F(data.Ch13.MultiplierFor(test.District)));
        if (test.Inputs.Ch13ProjectedPlanPayment is { } payment)
        {
            values["ch13_plan_payment"] = new TextFill(FormatMoney(payment));
        }

        // Line 33c's box is the widget of 13e's first creditor row.
        if (byLine.TryGetValue("13e", out var total) && Exact.Dec(total) != 0)
        {
            RowFill(release, values, "vehicle_2_loan_rows", 0, new TextFill(FormatMoney(Exact.Dec(total))), problems);
        }

        EnteredRows(release, values, caseFile, problems);
        var inputs = test.Inputs;
        if (inputs.VehicleCount is { } vehicles)
        {
            values["vehicle_count"] = new OptionFill(vehicles switch
            {
                0 => "14",
                1 => "12",
                2 => "On",
                _ => throw new EngineKeyException(vehicles.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            });
        }

        values["cure_claimed"] = new OptionFill(inputs.PriorityCureTotal is not null ? "yes" : "no");
        values["priority_claims_owed"] = new OptionFill(Exact.Dec(test.PriorityDebtTotal) > 0 ? "yes" : "no");
        B122A1.FillSignatureDates(values, caseFile);

        if (problems.Count > 0)
        {
            throw new FormProjectionException(problems);
        }

        return values;
    }

    private static (MeansTestCase Test, MeansTestResult Result, MeansTestData Data) Run(CaseFile caseFile)
    {
        var test = B122A2.BuildMeansTestCase(caseFile);
        if (test.Cmi.Problems.Count > 0)
        {
            throw new FormProjectionException(test.Cmi.Problems);
        }

        try
        {
            var data = MeansTestEngine.ResolveData(B122A1.MeansTestAsOf(caseFile).AsOf);
            return (test, MeansTestEngine.Run(test, data), data);
        }
        catch (MeansTestException error)
        {
            throw new FormProjectionException(error.Problems);
        }
        catch (EngineLookupException error)
        {
            throw new FormProjectionException([Exact.Str(error)]);
        }
    }

    private static void EnteredRows(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        if (caseFile.MeansTestInputs.Count == 0)
        {
            return;
        }

        var inputs = caseFile.MeansTestInputs[0];
        for (var index = 0; index < inputs.OtherSecuredPayments.Count; index++)
        {
            var payment = inputs.OtherSecuredPayments[index];
            RowFill(release, values, "other_secured_creditor", index, TextOrNone(payment.CreditorName), problems);
            RowFill(release, values, "other_secured_property", index, TextOrNone(payment.PropertyDescription), problems);
            RowFill(release, values, "other_secured_monthly", index, payment.MonthlyPayment is { } monthly ? new TextFill(FormatMoney(monthly)) : null, problems);
        }

        for (var index = 0; index < inputs.SpecialCircumstances.Count; index++)
        {
            var item = inputs.SpecialCircumstances[index];
            RowFill(release, values, "special_circumstances_description", index, TextOrNone(item.Description), problems);
            RowFill(release, values, "special_circumstances_amount", index, item.Amount is { } amount ? new TextFill(FormatMoney(amount)) : null, problems);
        }

        if (!string.IsNullOrEmpty(inputs.HousingAdjustmentExplanation))
        {
            RowFill(release, values, "housing_adjustment_explanation", 0, new TextFill(inputs.HousingAdjustmentExplanation), problems);
        }

        foreach (var (fieldId, entered) in new[] { ("health_insurance", inputs.HealthInsurance), ("disability_insurance", inputs.DisabilityInsurance), ("health_savings_account", inputs.HealthSavingsAccount) })
        {
            if (entered is { } value)
            {
                values[fieldId] = new TextFill(FormatMoney(value));
            }
        }
    }
}
