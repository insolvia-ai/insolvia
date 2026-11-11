using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Income;

// From income.py: 106I's employment and income summary, and the dated income
// history behind the means test (§ 101(10A)) — per-paycheck records and dated
// non-wage receipts. "106I is not the income model" (case-data-model.md).

/// <summary>106I Part 1: one job of one debtor.</summary>
public sealed record EmploymentBody(
    [property: Text(64)] string? DebtorId = null,
    EmploymentStatus? Status = null,
    [property: Text] string? Occupation = null,
    [property: Text] string? EmployerName = null,
    Address? EmployerAddress = null,
    DateOnly? EmployedSince = null)
{
    /// <summary>The employer's address; an empty one when none was given.</summary>
    public Address EmployerAddress { get; init; } = EmployerAddress ?? new();
}

/// <summary>
/// One itemised deduction line on a stub; carries its own client-chosen id so
/// provenance addresses it as <c>deductions[&lt;id&gt;].amount</c>.
/// </summary>
public sealed record PayPeriodDeduction(
    string Id,
    DeductionCategory? Category = null,
    [property: Money] decimal? Amount = null,
    [property: Text] string? Description = null) : IRow;

/// <summary>
/// One paycheck. The three dates are distinct facts; the means test's
/// lookback keys on <see cref="PayDate"/>.
/// </summary>
public sealed record PayPeriodRecordBody(
    [property: Text(64)] string? EmploymentId = null,
    DateOnly? PeriodStart = null,
    DateOnly? PeriodEnd = null,
    DateOnly? PayDate = null,
    [property: Money] decimal? Gross = null,
    [property: Money] decimal? Net = null,
    PayFrequency? Frequency = null,
    ValueCollection<PayPeriodDeduction>? Deductions = null)
{
    /// <summary>The stub's itemised deductions.</summary>
    [Rows]
    public ValueCollection<PayPeriodDeduction> Deductions { get; init; } = Deductions ?? [];
}

/// <summary>
/// One dated non-wage receipt (issue #100). <see cref="Expenses"/> are the
/// operating expenses of the same receipt, meaningful only for
/// <c>business</c> and <c>rental</c> (B122A-1 lines 5-6). An excluded
/// category is still a record — the derivation shows it excluded.
/// </summary>
public sealed record OtherIncomeRecordBody(
    [property: Text(64)] string? DebtorId = null,
    OtherIncomeCategory? Category = null,
    DateOnly? ReceivedOn = null,
    [property: Money] decimal? Amount = null,
    [property: Money] decimal? Expenses = null,
    [property: Text] string? Payer = null,
    [property: Text] string? Description = null) : IValidatableObject
{
    /// <summary>Operating expenses belong only to a business or rental receipt.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Expenses is not null && Category is not (null or OtherIncomeCategory.Business or OtherIncomeCategory.Rental))
        {
            yield return new ValidationResult("Only business and rental receipts carry operating expenses (B122A-1 lines 5-6).", [nameof(Expenses)]);
        }
    }
}

/// <summary>106I Part 2, one column — every money member a monthly estimate as of filing.</summary>
public sealed record IncomeSummaryBody(
    [property: Text(64)] string? DebtorId = null,
    [property: Money] decimal? Wages = null,
    [property: Money] decimal? Overtime = null,
    [property: Money] decimal? DeductionTax = null,
    [property: Money] decimal? DeductionMandatoryRetirement = null,
    [property: Money] decimal? DeductionVoluntaryRetirement = null,
    [property: Money] decimal? DeductionRetirementLoanRepayment = null,
    [property: Money] decimal? DeductionInsurance = null,
    [property: Money] decimal? DeductionDomesticSupport = null,
    [property: Money] decimal? DeductionUnionDues = null,
    [property: Money] decimal? DeductionOther = null,
    [property: Text] string? DeductionOtherSpecify = null,
    [property: Money] decimal? BusinessNetIncome = null,
    [property: Money] decimal? InterestAndDividends = null,
    [property: Money] decimal? FamilySupport = null,
    [property: Money] decimal? Unemployment = null,
    [property: Money] decimal? SocialSecurity = null,
    [property: Money] decimal? OtherGovernmentAssistance = null,
    [property: Text] string? OtherGovernmentAssistanceSpecify = null,
    [property: Money] decimal? PensionOrRetirement = null,
    [property: Money] decimal? OtherMonthlyIncome = null,
    [property: Text] string? OtherMonthlyIncomeSpecify = null,
    [property: Money] decimal? HouseholdContributions = null,
    [property: Text] string? HouseholdContributionsSpecify = null,
    bool? ChangeExpected = null,
    [property: Narrative] string? ChangeExplanation = null);

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
    public static readonly EntityKind<EmploymentBody> Employment = new("employment", "employments", "EMPLOYMENT");

    /// <summary>The pay-period collection's kind.</summary>
    public static readonly EntityKind<PayPeriodRecordBody> PayPeriodRecord = new("pay_period_record", "pay_period_records", "PAY_PERIOD");

    /// <summary>The other-income collection's kind.</summary>
    public static readonly EntityKind<OtherIncomeRecordBody> OtherIncomeRecord = new("other_income_record", "other_income_records", "OTHER_INCOME");

    /// <summary>The income-summary collection's kind.</summary>
    public static readonly EntityKind<IncomeSummaryBody> IncomeSummary = new("income_summary", "income_summaries", "INCOME_SUMMARY");
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
