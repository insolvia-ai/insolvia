using System.Text.RegularExpressions;
using Insolvia.Core.Debtors;
using Insolvia.Core.Income;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B106I @ 2015-12-01 — Schedule I's mapping (from <c>form_projections/b106i.py</c>):
/// two debtor columns (the second may be a non-filing spouse) and the
/// arithmetic the model refuses to store — line 4 = 2 + 3, line 6 sums the
/// deductions, 7 = 4 - 6, 9 sums the other income, 10 = 7 + 9, line 12 adds
/// Debtor 1's household contributions.
/// </summary>
public static class B106I
{
    private static readonly Dictionary<EmploymentStatus, string> EmploymentExports = new()
    {
        [EmploymentStatus.Employed] = "employed",
        [EmploymentStatus.NotEmployed] = "unemployed",
    };

    private static readonly (string Line, Func<IncomeSummaryBody, decimal?> Read)[] DeductionLines =
    [
        ("5a_tax_medicare_ss", s => s.DeductionTax),
        ("5b_mandatory_retirement", s => s.DeductionMandatoryRetirement),
        ("5c_voluntary_retirement", s => s.DeductionVoluntaryRetirement),
        ("5d_retirement_loan_repayment", s => s.DeductionRetirementLoanRepayment),
        ("5e_insurance", s => s.DeductionInsurance),
        ("5f_domestic_support", s => s.DeductionDomesticSupport),
        ("5g_union_dues", s => s.DeductionUnionDues),
        ("5h_other_deductions", s => s.DeductionOther),
    ];

    private static readonly (string Line, Func<IncomeSummaryBody, decimal?> Read)[] OtherIncomeLines =
    [
        ("8a_rental_business_net", s => s.BusinessNetIncome),
        ("8b_interest_dividends", s => s.InterestAndDividends),
        ("8c_family_support", s => s.FamilySupport),
        ("8d_unemployment", s => s.Unemployment),
        ("8e_social_security", s => s.SocialSecurity),
        ("8f_government_assistance", s => s.OtherGovernmentAssistance),
        ("8g_pension_retirement", s => s.PensionOrRetirement),
        ("8h_other_income", s => s.OtherMonthlyIncome),
    ];

    /// <summary>
    /// 106I line 12 — combined monthly income plus household contributions,
    /// exactly as the projection derives it; 106Sum line 4 and 106J line 23a copy it.
    /// </summary>
    public static decimal MonthlyIncomeLine12(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        var total = 0m;
        IncomeSummaryBody? summary1 = null;
        foreach (var debtor in caseFile.Debtors)
        {
            var summary = caseFile.IncomeSummaries.FirstOrDefault(s => s.DebtorId == debtor.Id);
            if (summary is null)
            {
                continue;
            }

            if (debtor.FilingRole == FilingRole.Debtor1)
            {
                summary1 = summary;
            }

            var gross = Amount(summary.Wages) + Amount(summary.Overtime);
            var deductions = DeductionLines.Aggregate(0m, (sum, line) => sum + Amount(line.Read(summary)));
            var other = OtherIncomeLines.Aggregate(0m, (sum, line) => sum + Amount(line.Read(summary)));
            total += gross - deductions + other;
        }

        if (summary1 is not null)
        {
            total += Amount(summary1.HouseholdContributions);
        }

        return total;
    }

    /// <summary>The values for <c>form/b106i@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var values = new FieldValues();
        var debtor1 = caseFile.Debtor(FilingRole.Debtor1);
        var debtor2 = caseFile.Debtor(FilingRole.Debtor2, FilingRole.NonFilingSpouse);

        values["caption.district"] = new TextFill(caseFile.Case.District);
        if (FullName(debtor1) is { } name1)
        {
            values["caption.debtor1_name"] = new TextFill(name1);
        }

        if (FullName(debtor2) is { } name2)
        {
            values["caption.debtor2_name"] = new TextFill(name2);
        }

        (string Digit, EmploymentBody? Employment, IncomeSummaryBody? Summary) Column(Debtor? debtor, string digit) =>
            debtor is null
                ? (digit, null, null)
                : (digit,
                    caseFile.Employments.Select(pair => pair.Body).FirstOrDefault(body => body.DebtorId == debtor.Id),
                    caseFile.IncomeSummaries.FirstOrDefault(s => s.DebtorId == debtor.Id));

        var columns = new[] { Column(debtor1, "1"), Column(debtor2, "2") };
        var monthlyTotals = new List<decimal>();

        foreach (var (digit, employment, summary) in columns)
        {
            if (employment is not null)
            {
                if (employment.Status is { } status)
                {
                    PutColumn(release, values, "employment_status", digit, new OptionFill(EmploymentExports[status]));
                }

                foreach (var (fieldId, value) in new[] { ("occupation", employment.Occupation), ("employer_name", employment.EmployerName) })
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        PutColumn(release, values, fieldId, digit, new TextFill(value));
                    }
                }

                if (employment.EmployedSince is { } since)
                {
                    PutColumn(release, values, "how_long_employed", digit, new TextFill($"Since {FormatDate(since)}"));
                }

                var address = employment.EmployerAddress;
                var spec = release.Field("employer_address");
                foreach (var (part, box) in new[]
                {
                    (address.Line1, $"Employers Street1 Debtor {digit}"),
                    (address.Line2, $"Employers Street2 Debtor {digit}"),
                    (address.City, $"Employers City Debtor {digit}"),
                    (address.State, $"Employers State Debtor {digit}"),
                    (address.PostalCode, $"Employers Zip debtor {digit}"),
                })
                {
                    if (!string.IsNullOrEmpty(part) && spec.PdfNames.Contains(box))
                    {
                        values.Rows("employer_address")[box] = new TextFill(part);
                    }
                }
            }

            if (summary is null)
            {
                continue;
            }

            void PutMoney(string fieldId, decimal value) => PutColumn(release, values, fieldId, digit, new TextFill(FormatMoney(value)));

            if (summary.Wages is not null)
            {
                PutMoney("line_2_gross_wages", Amount(summary.Wages));
            }

            if (summary.Overtime is not null)
            {
                PutMoney("line_3_overtime", Amount(summary.Overtime));
            }

            var gross = Amount(summary.Wages) + Amount(summary.Overtime);
            PutMoney("line_4_gross_income", gross);
            PutMoney("line_4_copy", gross);

            var deductions = 0m;
            foreach (var (line, read) in DeductionLines)
            {
                var stored = read(summary);
                if (stored is not null)
                {
                    PutMoney($"line_{line}", Amount(stored));
                }

                deductions += Amount(stored);
            }

            PutMoney("line_6_total_deductions", deductions);
            var takeHome = gross - deductions;
            PutMoney("line_7_take_home_pay", takeHome);

            var otherIncome = 0m;
            foreach (var (line, read) in OtherIncomeLines)
            {
                var stored = read(summary);
                if (stored is not null)
                {
                    PutMoney($"line_{line}", Amount(stored));
                }

                otherIncome += Amount(stored);
            }

            PutMoney("line_9_total_other_income", otherIncome);
            var monthly = takeHome + otherIncome;
            PutMoney("line_10_monthly_income", monthly);
            monthlyTotals.Add(monthly);

            if (!string.IsNullOrEmpty(summary.DeductionOtherSpecify))
            {
                values["line_5h_other_deductions_specify"] = new TextFill(summary.DeductionOtherSpecify);
            }

            if (!string.IsNullOrEmpty(summary.OtherGovernmentAssistanceSpecify))
            {
                values["line_8f_government_assistance_specify"] = new TextFill(summary.OtherGovernmentAssistanceSpecify);
            }

            if (!string.IsNullOrEmpty(summary.OtherMonthlyIncomeSpecify))
            {
                values["line_8h_other_income_specify"] = new TextFill(summary.OtherMonthlyIncomeSpecify);
            }
        }

        if (monthlyTotals.Count > 0)
        {
            var combined = monthlyTotals.Aggregate(0m, (sum, monthly) => sum + monthly);
            values["line_10_combined"] = new TextFill(FormatMoney(combined));

            var summary1 = columns[0].Summary;
            var contributions = Amount(summary1?.HouseholdContributions);
            if (summary1?.HouseholdContributions is not null)
            {
                values["line_11_household_contributions"] = new TextFill(FormatMoney(contributions));
            }

            values["line_12_combined_monthly_income"] = new TextFill(FormatMoney(combined + contributions));

            if (summary1?.ChangeExpected is { } change)
            {
                values["line_13_change_expected"] = YesNo(release, "line_13_change_expected", change);
                if (change && !string.IsNullOrEmpty(summary1.ChangeExplanation))
                {
                    values["line_13_change_explanation"] = new TextFill(summary1.ChangeExplanation);
                }
            }
        }

        return values;
    }

    private static string ColumnWidget(FormRelease release, string fieldId, string digit)
    {
        var pattern = new Regex($"[Dd]ebtor {digit}a?$", RegexOptions.CultureInvariant);
        var matches = release.Field(fieldId).PdfNames.Where(name => pattern.IsMatch(name)).ToList();
        if (matches.Count != 1)
        {
            throw new EngineKeyException($"{fieldId} has no single debtor-{digit} column");
        }

        return matches[0];
    }

    private static void PutColumn(FormRelease release, FieldValues values, string fieldId, string digit, FieldFill fill)
    {
        var spec = release.Field(fieldId);
        if (spec.PdfNames.Count == 1)
        {
            values[fieldId] = fill;
            return;
        }

        values.Rows(fieldId)[ColumnWidget(release, fieldId, digit)] = fill;
    }
}
