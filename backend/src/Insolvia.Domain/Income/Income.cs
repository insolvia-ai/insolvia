using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Income;

// From income.py: 106I's employment and income summary, and the dated income
// history behind the means test (§ 101(10A)) — per-paycheck records and dated
// non-wage receipts. "106I is not the income model" (case-data-model.md).

/// <summary>106I Part 1: one job of one debtor.</summary>
public sealed record EmploymentBody(
    string? DebtorId,
    EmploymentStatus? Status,
    string? Occupation,
    string? EmployerName,
    Address EmployerAddress,
    DateOnly? EmployedSince);

/// <summary>
/// One itemised deduction line on a stub; carries its own client-chosen id so
/// provenance addresses it as <c>deductions[&lt;id&gt;].amount</c>.
/// </summary>
public sealed record PayPeriodDeduction(string Id, DeductionCategory? Category, decimal? Amount, string? Description);

/// <summary>
/// One paycheck. The three dates are distinct facts; the means test's
/// lookback keys on <see cref="PayDate"/>.
/// </summary>
public sealed record PayPeriodRecordBody(
    string? EmploymentId,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    DateOnly? PayDate,
    decimal? Gross,
    decimal? Net,
    PayFrequency? Frequency,
    ValueCollection<PayPeriodDeduction> Deductions);

/// <summary>
/// One dated non-wage receipt (issue #100). <see cref="Expenses"/> are the
/// operating expenses of the same receipt, meaningful only for
/// <c>business</c> and <c>rental</c> (B122A-1 lines 5-6). An excluded
/// category is still a record — the derivation shows it excluded.
/// </summary>
public sealed record OtherIncomeRecordBody(
    string? DebtorId,
    OtherIncomeCategory? Category,
    DateOnly? ReceivedOn,
    decimal? Amount,
    decimal? Expenses,
    string? Payer,
    string? Description);

/// <summary>106I Part 2, one column — every money member a monthly estimate as of filing.</summary>
public sealed record IncomeSummaryBody(
    string? DebtorId,
    decimal? Wages,
    decimal? Overtime,
    decimal? DeductionTax,
    decimal? DeductionMandatoryRetirement,
    decimal? DeductionVoluntaryRetirement,
    decimal? DeductionRetirementLoanRepayment,
    decimal? DeductionInsurance,
    decimal? DeductionDomesticSupport,
    decimal? DeductionUnionDues,
    decimal? DeductionOther,
    string? DeductionOtherSpecify,
    decimal? BusinessNetIncome,
    decimal? InterestAndDividends,
    decimal? FamilySupport,
    decimal? Unemployment,
    decimal? SocialSecurity,
    decimal? OtherGovernmentAssistance,
    string? OtherGovernmentAssistanceSpecify,
    decimal? PensionOrRetirement,
    decimal? OtherMonthlyIncome,
    string? OtherMonthlyIncomeSpecify,
    decimal? HouseholdContributions,
    string? HouseholdContributionsSpecify,
    bool? ChangeExpected,
    string? ChangeExplanation);

/// <summary>The four income collections.</summary>
public static class IncomeRules
{
    /// <summary>The CMI categories (Python's <c>OTHER_INCOME_CATEGORIES</c>).</summary>
    public static readonly IReadOnlyList<OtherIncomeCategory> IncludedCategories =
    [
        OtherIncomeCategory.AlimonyMaintenance,
        OtherIncomeCategory.HouseholdContributions,
        OtherIncomeCategory.Business,
        OtherIncomeCategory.Rental,
        OtherIncomeCategory.InterestDividendsRoyalties,
        OtherIncomeCategory.Unemployment,
        OtherIncomeCategory.PensionRetirement,
        OtherIncomeCategory.Other,
    ];

    /// <summary>The § 101(10A)(B)(ii) exclusions (Python's <c>EXCLUDED_INCOME_CATEGORIES</c>).</summary>
    public static readonly IReadOnlyList<OtherIncomeCategory> ExcludedCategories =
    [
        OtherIncomeCategory.SocialSecurityActBenefit,
        OtherIncomeCategory.VeteransDisabilityCompensation,
        OtherIncomeCategory.WarCrimeVictimPayment,
        OtherIncomeCategory.TerrorismVictimPayment,
    ];

    /// <summary>The employment collection's kind.</summary>
    public static readonly EntityKind<EmploymentBody> Employment = new("employment", "employments", "EMPLOYMENT", ParseEmployment);

    /// <summary>The pay-period collection's kind.</summary>
    public static readonly EntityKind<PayPeriodRecordBody> PayPeriodRecord = new("pay_period_record", "pay_period_records", "PAY_PERIOD", ParsePayPeriodRecord);

    /// <summary>The other-income collection's kind.</summary>
    public static readonly EntityKind<OtherIncomeRecordBody> OtherIncomeRecord = new("other_income_record", "other_income_records", "OTHER_INCOME", ParseOtherIncomeRecord);

    /// <summary>The income-summary collection's kind.</summary>
    public static readonly EntityKind<IncomeSummaryBody> IncomeSummary = new("income_summary", "income_summaries", "INCOME_SUMMARY", ParseIncomeSummary);

    /// <summary>Validates an employment body (Python's <c>parse_employment</c>).</summary>
    public static EmploymentBody ParseEmployment(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new EmploymentBody(
            DebtorId: FieldParsers.Text(payload["debtor_id"], "debtor_id", errors, limit: 64),
            Status: FieldParsers.Choice<EmploymentStatus>(payload["status"], "status", errors),
            Occupation: FieldParsers.Text(payload["occupation"], "occupation", errors),
            EmployerName: FieldParsers.Text(payload["employer_name"], "employer_name", errors),
            EmployerAddress: FieldParsers.ParseAddress(payload["employer_address"], "employer_address", errors),
            EmployedSince: FieldParsers.FormDate(payload["employed_since"], "employed_since", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>Validates a pay-period body (Python's <c>parse_pay_period_record</c>).</summary>
    public static PayPeriodRecordBody ParsePayPeriodRecord(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new PayPeriodRecordBody(
            EmploymentId: FieldParsers.Text(payload["employment_id"], "employment_id", errors, limit: 64),
            PeriodStart: FieldParsers.FormDate(payload["period_start"], "period_start", errors),
            PeriodEnd: FieldParsers.FormDate(payload["period_end"], "period_end", errors),
            PayDate: FieldParsers.FormDate(payload["pay_date"], "pay_date", errors),
            Gross: FieldParsers.Money(payload["gross"], "gross", errors),
            Net: FieldParsers.Money(payload["net"], "net", errors),
            Frequency: FieldParsers.Choice<PayFrequency>(payload["frequency"], "frequency", errors),
            Deductions: IdRows.Parse(
                payload["deductions"],
                "deductions",
                errors,
                (id, raw, path) => new PayPeriodDeduction(
                    id,
                    FieldParsers.Choice<DeductionCategory>(raw["category"], $"{path}.category", errors),
                    FieldParsers.Money(raw["amount"], $"{path}.amount", errors),
                    FieldParsers.Text(raw["description"], $"{path}.description", errors)),
                skipNonObjects: false));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>
    /// Validates an other-income body (Python's <c>parse_other_income_record</c>).
    /// Operating expenses belong only to a business or rental receipt.
    /// </summary>
    public static OtherIncomeRecordBody ParseOtherIncomeRecord(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var category = FieldParsers.Choice<OtherIncomeCategory>(payload["category"], "category", errors);
        var expenses = FieldParsers.Money(payload["expenses"], "expenses", errors);
        if (expenses is not null && category is not (null or OtherIncomeCategory.Business or OtherIncomeCategory.Rental))
        {
            errors["expenses"] = "Only business and rental receipts carry operating expenses (B122A-1 lines 5-6).";
        }

        var body = new OtherIncomeRecordBody(
            DebtorId: FieldParsers.Text(payload["debtor_id"], "debtor_id", errors, limit: 64),
            Category: category,
            ReceivedOn: FieldParsers.FormDate(payload["received_on"], "received_on", errors),
            Amount: FieldParsers.Money(payload["amount"], "amount", errors),
            Expenses: expenses,
            Payer: FieldParsers.Text(payload["payer"], "payer", errors),
            Description: FieldParsers.Text(payload["description"], "description", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>Validates an income-summary body (Python's <c>parse_income_summary</c>).</summary>
    public static IncomeSummaryBody ParseIncomeSummary(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        decimal? Amount(string key) => FieldParsers.Money(payload[key], key, errors);
        var body = new IncomeSummaryBody(
            DebtorId: FieldParsers.Text(payload["debtor_id"], "debtor_id", errors, limit: 64),
            Wages: Amount("wages"),
            Overtime: Amount("overtime"),
            DeductionTax: Amount("deduction_tax"),
            DeductionMandatoryRetirement: Amount("deduction_mandatory_retirement"),
            DeductionVoluntaryRetirement: Amount("deduction_voluntary_retirement"),
            DeductionRetirementLoanRepayment: Amount("deduction_retirement_loan_repayment"),
            DeductionInsurance: Amount("deduction_insurance"),
            DeductionDomesticSupport: Amount("deduction_domestic_support"),
            DeductionUnionDues: Amount("deduction_union_dues"),
            DeductionOther: Amount("deduction_other"),
            DeductionOtherSpecify: FieldParsers.Text(payload["deduction_other_specify"], "deduction_other_specify", errors),
            BusinessNetIncome: Amount("business_net_income"),
            InterestAndDividends: Amount("interest_and_dividends"),
            FamilySupport: Amount("family_support"),
            Unemployment: Amount("unemployment"),
            SocialSecurity: Amount("social_security"),
            OtherGovernmentAssistance: Amount("other_government_assistance"),
            OtherGovernmentAssistanceSpecify: FieldParsers.Text(payload["other_government_assistance_specify"], "other_government_assistance_specify", errors),
            PensionOrRetirement: Amount("pension_or_retirement"),
            OtherMonthlyIncome: Amount("other_monthly_income"),
            OtherMonthlyIncomeSpecify: FieldParsers.Text(payload["other_monthly_income_specify"], "other_monthly_income_specify", errors),
            HouseholdContributions: Amount("household_contributions"),
            HouseholdContributionsSpecify: FieldParsers.Text(payload["household_contributions_specify"], "household_contributions_specify", errors),
            ChangeExpected: FieldParsers.Boolean(payload["change_expected"], "change_expected", errors),
            ChangeExplanation: FieldParsers.Narrative(payload["change_explanation"], "change_explanation", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }
}

/// <summary>106I line 1's employment status (Python's <c>EMPLOYMENT_STATUSES</c>).</summary>
public enum EmploymentStatus
{
    /// <summary>The wire value <c>employed</c>.</summary>
    [JsonStringEnumMemberName("employed")]
    Employed,

    /// <summary>The wire value <c>not_employed</c>.</summary>
    [JsonStringEnumMemberName("not_employed")]
    NotEmployed,
}

/// <summary>A pay stub's itemised deduction (Python's <c>DEDUCTION_CATEGORIES</c>).</summary>
public enum DeductionCategory
{
    /// <summary>The wire value <c>tax</c>.</summary>
    [JsonStringEnumMemberName("tax")]
    Tax,

    /// <summary>The wire value <c>mandatory_retirement</c>.</summary>
    [JsonStringEnumMemberName("mandatory_retirement")]
    MandatoryRetirement,

    /// <summary>The wire value <c>voluntary_retirement</c>.</summary>
    [JsonStringEnumMemberName("voluntary_retirement")]
    VoluntaryRetirement,

    /// <summary>The wire value <c>retirement_loan_repayment</c>.</summary>
    [JsonStringEnumMemberName("retirement_loan_repayment")]
    RetirementLoanRepayment,

    /// <summary>The wire value <c>insurance</c>.</summary>
    [JsonStringEnumMemberName("insurance")]
    Insurance,

    /// <summary>The wire value <c>domestic_support</c>.</summary>
    [JsonStringEnumMemberName("domestic_support")]
    DomesticSupport,

    /// <summary>The wire value <c>union_dues</c>.</summary>
    [JsonStringEnumMemberName("union_dues")]
    UnionDues,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>How often a paycheck comes (Python's <c>PAY_FREQUENCIES</c>).</summary>
public enum PayFrequency
{
    /// <summary>The wire value <c>weekly</c>.</summary>
    [JsonStringEnumMemberName("weekly")]
    Weekly,

    /// <summary>The wire value <c>biweekly</c>.</summary>
    [JsonStringEnumMemberName("biweekly")]
    Biweekly,

    /// <summary>The wire value <c>semimonthly</c>.</summary>
    [JsonStringEnumMemberName("semimonthly")]
    Semimonthly,

    /// <summary>The wire value <c>monthly</c>.</summary>
    [JsonStringEnumMemberName("monthly")]
    Monthly,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>A non-wage receipt's category: the CMI categories, then the § 101(10A)(B)(ii) exclusions (Python's <c>OTHER_INCOME_CATEGORIES + EXCLUDED_INCOME_CATEGORIES</c>).</summary>
public enum OtherIncomeCategory
{
    /// <summary>The wire value <c>alimony_maintenance</c>.</summary>
    [JsonStringEnumMemberName("alimony_maintenance")]
    AlimonyMaintenance,

    /// <summary>The wire value <c>household_contributions</c>.</summary>
    [JsonStringEnumMemberName("household_contributions")]
    HouseholdContributions,

    /// <summary>The wire value <c>business</c>.</summary>
    [JsonStringEnumMemberName("business")]
    Business,

    /// <summary>The wire value <c>rental</c>.</summary>
    [JsonStringEnumMemberName("rental")]
    Rental,

    /// <summary>The wire value <c>interest_dividends_royalties</c>.</summary>
    [JsonStringEnumMemberName("interest_dividends_royalties")]
    InterestDividendsRoyalties,

    /// <summary>The wire value <c>unemployment</c>.</summary>
    [JsonStringEnumMemberName("unemployment")]
    Unemployment,

    /// <summary>The wire value <c>pension_retirement</c>.</summary>
    [JsonStringEnumMemberName("pension_retirement")]
    PensionRetirement,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,

    /// <summary>The wire value <c>social_security_act_benefit</c>.</summary>
    [JsonStringEnumMemberName("social_security_act_benefit")]
    SocialSecurityActBenefit,

    /// <summary>The wire value <c>veterans_disability_compensation</c>.</summary>
    [JsonStringEnumMemberName("veterans_disability_compensation")]
    VeteransDisabilityCompensation,

    /// <summary>The wire value <c>war_crime_victim_payment</c>.</summary>
    [JsonStringEnumMemberName("war_crime_victim_payment")]
    WarCrimeVictimPayment,

    /// <summary>The wire value <c>terrorism_victim_payment</c>.</summary>
    [JsonStringEnumMemberName("terrorism_victim_payment")]
    TerrorismVictimPayment,
}
