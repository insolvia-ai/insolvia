using Insolvia.Core;
using Insolvia.Core.Claims;
using Insolvia.Core.Debtors;
using Insolvia.Core.Expenses;
using Insolvia.Core.Petitions;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B106J @ 2015-12-01 — Schedule J's mapping (from <c>form_projections/b106j.py</c>).
/// Expenses are rows keyed by category; a one-box category sums its rows,
/// the two-row pairs take one row each. 22a-22c and line 23 are derived,
/// line 23's income side through <see cref="B106I.MonthlyIncomeLine12"/>.
/// The household block is shared with 106J-2.
/// </summary>
public static class B106J
{
    private static readonly (string Category, string AmountField, string? SpecifyField)[] SingleLines =
    [
        ("rent_or_home_ownership", "line_4_rent_or_home_ownership", null),
        ("real_estate_taxes", "line_4a_real_estate_taxes", null),
        ("property_insurance", "line_4b_property_insurance", null),
        ("home_maintenance", "line_4c_home_maintenance", null),
        ("homeowners_association_dues", "line_4d_hoa_dues", null),
        ("additional_mortgage_payments", "line_5_additional_mortgage", null),
        ("electricity_heat_gas", "line_6a_electricity_heat_gas", null),
        ("water_sewer_garbage", "line_6b_water_sewer_garbage", null),
        ("telephone_and_internet", "line_6c_telecom", null),
        ("other_utilities", "line_6d_utilities_other", "line_6d_utilities_other_specify"),
        ("food_and_housekeeping", "line_7_food_housekeeping", null),
        ("childcare_and_education", "line_8_childcare_education", null),
        ("clothing_and_laundry", "line_9_clothing_laundry", null),
        ("personal_care", "line_10_personal_care", null),
        ("medical_and_dental", "line_11_medical_dental", null),
        ("transportation", "line_12_transportation", null),
        ("entertainment_and_recreation", "line_13_entertainment", null),
        ("charitable_contributions", "line_14_charitable", null),
        ("life_insurance", "line_15a_life_insurance", null),
        ("health_insurance", "line_15b_health_insurance", null),
        ("vehicle_insurance", "line_15c_vehicle_insurance", null),
        ("other_insurance", "line_15d_other_insurance", "line_15d_other_insurance_specify"),
        ("taxes", "line_16_taxes", "line_16_taxes_specify"),
        ("alimony_and_support_payments", "line_18_alimony_support", null),
        ("support_of_others", "line_19_support_others", "line_19_support_others_specify"),
        ("other_property_mortgages", "line_20a_other_mortgages", null),
        ("other_property_taxes", "line_20b_other_real_estate_taxes", null),
        ("other_property_insurance", "line_20c_other_property_insurance", null),
        ("other_property_maintenance", "line_20d_other_property_maintenance", null),
        ("other_property_association_dues", "line_20e_other_property_hoa", null),
        ("other", "line_21_other", "line_21_other_specify"),
    ];

    private static readonly (string Category, (string AmountField, string? SpecifyField)[] Lines)[] PairedLines =
    [
        ("vehicle_installment_payments", [("line_17a_car_payment_vehicle1", null), ("line_17b_car_payment_vehicle2", null)]),
        ("other_installment_payments", [("line_17c_installment_other_1", "line_17c_installment_other_1_specify"), ("line_17d_installment_other_2", "line_17d_installment_other_2_specify")]),
    ];

    /// <summary>The household row of this kind, with its id, or null.</summary>
    public static (string Id, HouseholdBody Body)? HouseholdRow(CaseFile caseFile, WhichHousehold which)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        foreach (var pair in caseFile.Households)
        {
            if (pair.Body.WhichHousehold == which)
            {
                return pair;
            }
        }

        return null;
    }

    /// <summary>One schedule's expense lines summed — 106J 22a (main), 106J-2 22 (Debtor 2's).</summary>
    public static decimal HouseholdExpenseTotal(CaseFile caseFile, WhichHousehold which) =>
        HouseholdRow(caseFile, which) is { } row
            ? HouseholdExpenses(caseFile, row.Id).Aggregate(0m, (sum, expense) => sum + Amount(expense.Amount))
            : 0m;

    /// <summary>106J line 22c — both households' expenses; feeds 106Sum line 5.</summary>
    public static decimal MonthlyExpensesLine22c(CaseFile caseFile) =>
        HouseholdExpenseTotal(caseFile, WhichHousehold.Main) + HouseholdExpenseTotal(caseFile, WhichHousehold.Debtor2Separate);

    /// <summary>The line set 106J and 106J-2 both print: dependents, line 3, lines 4-21 and 24.</summary>
    public static void HouseholdBlock(FormRelease release, FieldValues values, CaseFile caseFile, string householdId, HouseholdBody household, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(caseFile);
        ArgumentNullException.ThrowIfNull(household);
        var dependents = caseFile.Dependents.Where(dependent => dependent.HouseholdId == householdId).ToList();
        values["line_2_have_dependents"] = YesNo(release, "line_2_have_dependents", dependents.Count > 0);
        for (var index = 0; index < dependents.Count; index++)
        {
            var dependent = dependents[index];
            RowFill(release, values, "line_2_dependent_relationship", index, TextOrNone(dependent.Relationship), problems);
            RowFill(release, values, "line_2_dependent_age", index, dependent.Age is { } age ? new TextFill(age.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null, problems);
            if (dependent.LivesWithDebtor is { } lives)
            {
                RowFill(release, values, "line_2_dependent_lives_with_you", index, new OptionFill(lives ? "yes" : "no"), problems);
            }
        }

        if (household.ExpensesIncludeOthers is { } includeOthers)
        {
            values["line_3_expenses_include_others"] = YesNo(release, "line_3_expenses_include_others", includeOthers);
        }

        var expenses = HouseholdExpenses(caseFile, householdId);
        foreach (var (category, amountField, specifyField) in SingleLines)
        {
            var matching = expenses.Where(expense => expense.Category?.ToWire() == category).ToList();
            if (matching.Count == 0)
            {
                continue;
            }

            values[amountField] = new TextFill(FormatMoney(matching.Aggregate(0m, (sum, expense) => sum + Amount(expense.Amount))));
            if (specifyField is not null)
            {
                var texts = matching.Select(expense => expense.SpecifyText).Where(text => !string.IsNullOrEmpty(text)).ToList();
                if (texts.Count > 0)
                {
                    values[specifyField] = new TextFill(string.Join("; ", texts));
                }
            }
        }

        foreach (var (category, linePair) in PairedLines)
        {
            var matching = expenses.Where(expense => expense.Category?.ToWire() == category).ToList();
            for (var index = 0; index < matching.Count; index++)
            {
                var expense = matching[index];
                if (index >= linePair.Length)
                {
                    problems.Add($"{category}: the form prints {linePair.Length} rows; row {index + 1} does not exist");
                    continue;
                }

                var (amountField, specifyField) = linePair[index];
                if (expense.Amount is { } amount)
                {
                    values[amountField] = new TextFill(FormatMoney(amount));
                }

                if (specifyField is not null && !string.IsNullOrEmpty(expense.SpecifyText))
                {
                    values[specifyField] = new TextFill(expense.SpecifyText);
                }
            }
        }

        if (household.ChangeExpected is { } change)
        {
            values["line_24_change_expected"] = YesNo(release, "line_24_change_expected", change);
            if (change && !string.IsNullOrEmpty(household.ChangeExplanation))
            {
                values["line_24_change_explanation"] = new TextFill(household.ChangeExplanation);
            }
        }
    }

    /// <summary>The values for <c>form/b106j@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        Caption(caseFile, values);

        var joint = caseFile.Debtor(FilingRole.Debtor2) is not null;
        values["line_1_joint_case"] = YesNo(release, "line_1_joint_case", joint);
        var main = HouseholdRow(caseFile, WhichHousehold.Main);
        if (joint && main is { } mainRow && mainRow.Body.SeparateHousehold is { } separate)
        {
            values["line_1_debtor2_separate_household"] = YesNo(release, "line_1_debtor2_separate_household", separate);
        }

        if (main is { } household)
        {
            HouseholdBlock(release, values, caseFile, household.Id, household.Body, problems);
        }

        var own = HouseholdExpenseTotal(caseFile, WhichHousehold.Main);
        var other = HouseholdExpenseTotal(caseFile, WhichHousehold.Debtor2Separate);
        values["line_22a_total_expenses"] = new TextFill(FormatMoney(own));
        if (HouseholdRow(caseFile, WhichHousehold.Debtor2Separate) is not null)
        {
            values["line_22b_debtor2_expenses"] = new TextFill(FormatMoney(other));
        }

        values["line_22c_monthly_expenses"] = new TextFill(FormatMoney(own + other));
        var income = B106I.MonthlyIncomeLine12(caseFile);
        values["line_23a_combined_monthly_income"] = new TextFill(FormatMoney(income));
        values["line_23b_monthly_expenses"] = new TextFill(FormatMoney(own + other));
        values["line_23c_net_income"] = new TextFill(FormatMoney(income - (own + other)));

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }

    private static List<ExpenseBody> HouseholdExpenses(CaseFile caseFile, string householdId) =>
        [.. caseFile.Expenses.Where(expense => expense.HouseholdId == householdId)];
}

/// <summary>
/// B106J-2 @ 2015-12-01 — the second household's Schedule J (from
/// <c>form_projections/b106j2.py</c>): 106J's household block over the
/// <c>debtor_2_separate</c> household; line 22 carries to 106J line 22b.
/// </summary>
public static class B106J2
{
    /// <summary>The values for <c>form/b106j2@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        Caption(caseFile, values);

        var second = B106J.HouseholdRow(caseFile, WhichHousehold.Debtor2Separate);
        values["line_1_separate_households"] = YesNo(release, "line_1_separate_households", second is not null);
        if (second is { } household)
        {
            B106J.HouseholdBlock(release, values, caseFile, household.Id, household.Body, problems);
            values["line_22_monthly_expenses"] = new TextFill(FormatMoney(B106J.HouseholdExpenseTotal(caseFile, WhichHousehold.Debtor2Separate)));
        }

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }
}

/// <summary>
/// B106Sum @ 2015-12-01 — the summary (from <c>form_projections/b106sum.py</c>):
/// every line a copy or sum of another schedule, through the same helpers
/// those schedules print from. Line 8 stays blank.
/// </summary>
public static class B106Sum
{
    /// <summary>The values for <c>form/b106sum@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var values = new FieldValues();
        Caption(caseFile, values);

        var realEstate = B106AB.RealEstateTotal(caseFile);
        var personal = B106AB.PersonalPropertyTotal(caseFile);
        values["line_1a_total_real_estate"] = new TextFill(FormatMoney(realEstate));
        values["line_1b_total_personal_property"] = new TextFill(FormatMoney(personal));
        values["line_1c_total_property"] = new TextFill(FormatMoney(realEstate + personal));

        var secured = B106D.SecuredTotal(caseFile);
        var priority = B106EF.PriorityUnsecuredTotal(caseFile);
        var nonpriority = B106EF.NonpriorityUnsecuredTotal(caseFile);
        values["line_2_secured_claims_total"] = new TextFill(FormatMoney(secured));
        values["line_3a_priority_unsecured_total"] = new TextFill(FormatMoney(priority));
        values["line_3b_nonpriority_unsecured_total"] = new TextFill(FormatMoney(nonpriority));
        values["line_3_total_liabilities"] = new TextFill(FormatMoney(secured + priority + nonpriority));

        values["line_4_combined_monthly_income"] = new TextFill(FormatMoney(B106I.MonthlyIncomeLine12(caseFile)));
        values["line_5_monthly_expenses"] = new TextFill(FormatMoney(B106J.MonthlyExpensesLine22c(caseFile)));

        values["line_6_filing_under_7_11_13"] = YesNo(release, "line_6_filing_under_7_11_13", caseFile.Case.Chapter is 7 or 11 or 13);
        var petition = caseFile.Petition;
        var consumer = petition?.DebtCharacter == DebtCharacter.Consumer;
        if (petition?.DebtCharacter is not null)
        {
            values["line_7_kind_of_debt"] = YesNo(release, "line_7_kind_of_debt", consumer);
        }

        if (consumer)
        {
            var domestic = B106EF.PriorityTypeTotal(caseFile, PriorityType.DomesticSupport);
            var taxes = B106EF.PriorityTypeTotal(caseFile, PriorityType.TaxAndGovernment);
            var intoxicated = B106EF.PriorityTypeTotal(caseFile, PriorityType.DeathOrInjuryWhileIntoxicated);
            var student = B106EF.NonpriorityTypeTotal(caseFile, NonpriorityType.StudentLoan);
            var divorce = B106EF.NonpriorityTypeTotal(caseFile, NonpriorityType.SeparationOrDivorce);
            var pension = B106EF.NonpriorityTypeTotal(caseFile, NonpriorityType.PensionOrProfitSharing);
            values["line_9a_domestic_support"] = new TextFill(FormatMoney(domestic));
            values["line_9b_taxes_government"] = new TextFill(FormatMoney(taxes));
            values["line_9c_intoxicated_injury"] = new TextFill(FormatMoney(intoxicated));
            values["line_9d_student_loans"] = new TextFill(FormatMoney(student));
            values["line_9e_separation_divorce"] = new TextFill(FormatMoney(divorce));
            values["line_9f_pension_profit_sharing"] = new TextFill(FormatMoney(pension));
            values["line_9g_total"] = new TextFill(FormatMoney(domestic + taxes + intoxicated + student + divorce + pension));
        }

        return values;
    }
}
