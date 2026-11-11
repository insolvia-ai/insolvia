using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.MeansTest;

// From means_test_inputs.py: B122A-2's (and B122C's) ENTERED figures, one
// record per case — the gate owns the cardinality. Everything derived is the
// calculator's, never stored.

/// <summary>One line-3 row: a part of the non-filing spouse's income not paid for the household.</summary>
public sealed record MaritalAdjustmentItem(string Id, [property: Text] string? Description = null, [property: Money] decimal? Amount = null) : IRow;

/// <summary>One B122C-2 line-43 row: a special circumstance and its monthly amount.</summary>
public sealed record SpecialCircumstanceItem(string Id, [property: Text] string? Description = null, [property: Money] decimal? Amount = null) : IRow;

/// <summary>One line-33d row: a secured debt beyond the home and the two claimed vehicles.</summary>
public sealed record OtherSecuredPayment(
    string Id,
    [property: Text] string? CreditorName = null,
    [property: Text] string? PropertyDescription = null,
    [property: Money] decimal? MonthlyPayment = null,
    [property: Text(64)] string? ClaimId = null,
    SecuredPaymentBucket? Bucket = null,
    [property: Money] decimal? CureTotal = null) : IRow;

/// <summary>One entered B122A-1 income line, replacing what the dated records derive for that column.</summary>
public sealed record IncomeLineOverride(
    string Id,
    IncomeColumn? Column = null,
    IncomeLineCategory? Category = null,
    [property: Money] decimal? MonthlyAmount = null) : IRow;

/// <summary>B122A-2's entered figures, named for their subjects; money is monthly unless the name says otherwise.</summary>
public sealed record MeansTestInputBody(
    [property: Count] int? PeopleUnder65 = null,
    [property: Count] int? People65OrOlder = null,
    [property: Count] int? MedianHouseholdSize = null,
    [property: Count] int? IrsFamilySize = null,
    [property: Count] int? IrsHousingFamilySize = null,
    MaritalFilingStatus? MaritalFilingStatus = null,
    bool? NonConsumerDebts = null,
    bool? DisabledVeteran = null,
    bool? ReservistNationalGuard = null,
    ValueCollection<IncomeLineOverride>? IncomeOverrides = null,
    ValueCollection<MaritalAdjustmentItem>? MaritalAdjustments = null,
    [property: Money] decimal? HomeSecuredMonthlyTotal = null,
    [property: Money] decimal? HousingAdjustmentAmount = null,
    [property: Text] string? HousingAdjustmentExplanation = null,
    [property: Count] int? VehicleCount = null,
    [property: Money] decimal? Vehicle1LoanMonthly = null,
    [property: Money] decimal? Vehicle2LoanMonthly = null,
    [property: Money] decimal? AdditionalPublicTransportation = null,
    [property: Money] decimal? Taxes = null,
    [property: Money] decimal? InvoluntaryDeductions = null,
    [property: Money] decimal? TermLifeInsurance = null,
    [property: Money] decimal? CourtOrderedPayments = null,
    [property: Money] decimal? EducationForEmploymentOrDisability = null,
    [property: Money] decimal? Childcare = null,
    [property: Money] decimal? HealthcareAboveAllowance = null,
    [property: Money] decimal? OptionalTelecom = null,
    [property: Money] decimal? HealthInsurance = null,
    [property: Money] decimal? DisabilityInsurance = null,
    [property: Money] decimal? HealthSavingsAccount = null,
    [property: Money] decimal? FamilyCareContributions = null,
    [property: Money] decimal? FamilyViolenceProtection = null,
    [property: Money] decimal? HomeEnergyExcess = null,
    [property: Money] decimal? EducationUnder18 = null,
    [property: Money] decimal? AdditionalFoodClothing = null,
    [property: Money] decimal? CharitableContributions = null,
    ValueCollection<OtherSecuredPayment>? OtherSecuredPayments = null,
    [property: Money] decimal? PriorityCureTotal = null,
    bool? Ch13Eligible = null,
    [property: Money] decimal? Ch13ProjectedPlanPayment = null,
    bool? CommitmentPeriodMaritalAdjustment = null,
    [property: Money] decimal? ChildSupportForDependents = null,
    [property: Money] decimal? QualifiedRetirementDeductions = null,
    ValueCollection<SpecialCircumstanceItem>? SpecialCircumstances = null) : IValidatableObject
{
    /// <summary>The entered income lines.</summary>
    [Rows]
    public ValueCollection<IncomeLineOverride> IncomeOverrides { get; init; } = IncomeOverrides ?? [];

    /// <summary>Line 3's adjustments.</summary>
    [Rows]
    public ValueCollection<MaritalAdjustmentItem> MaritalAdjustments { get; init; } = MaritalAdjustments ?? [];

    /// <summary>Line 33d's other secured debts.</summary>
    [Rows]
    public ValueCollection<OtherSecuredPayment> OtherSecuredPayments { get; init; } = OtherSecuredPayments ?? [];

    /// <summary>B122C-2 line 43's special circumstances.</summary>
    [Rows]
    public ValueCollection<SpecialCircumstanceItem> SpecialCircumstances { get; init; } = SpecialCircumstances ?? [];

    /// <summary>At most two vehicles; a household has a person; one override per column and line.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (VehicleCount is > MeansTestInputRules.MaxClaimedVehicles)
        {
            yield return new ValidationResult("The Local Standards publish figures for at most two vehicles — the form's own \"2 or more\".", [nameof(VehicleCount)]);
        }

        foreach (var (member, size) in new[] { (nameof(MedianHouseholdSize), MedianHouseholdSize), (nameof(IrsFamilySize), IrsFamilySize), (nameof(IrsHousingFamilySize), IrsHousingFamilySize) })
        {
            if (size == 0)
            {
                yield return new ValidationResult("A household has at least one person.", [member]);
            }
        }

        var seen = new HashSet<(IncomeColumn, IncomeLineCategory)>();
        for (var index = 0; index < IncomeOverrides.Count; index++)
        {
            if (IncomeOverrides[index] is { Column: { } column, Category: { } category } && !seen.Add((column, category)))
            {
                yield return new ValidationResult("This column already has an override for that line.", [$"incomeOverrides[{index}].category"]);
            }
        }
    }
}

/// <summary>The means-test-input collection.</summary>
public static class MeansTestInputRules
{
    /// <summary>The Local Standards publish figures for at most two vehicles.</summary>
    public const int MaxClaimedVehicles = 2;

    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<MeansTestInputBody> Kind = new("means_test_input", "means_test_inputs", "MEANS_TEST_INPUT");
}

/// <summary>B122A-1 line 1's marital and filing status (Python's <c>MARITAL_FILING_STATUSES</c>).</summary>
public enum MaritalFilingStatus
{
    /// <summary>The wire value <c>not_married</c>.</summary>
    [JsonStringEnumMemberName("not_married")]
    NotMarried,

    /// <summary>The wire value <c>married_filing_jointly</c>.</summary>
    [JsonStringEnumMemberName("married_filing_jointly")]
    MarriedFilingJointly,

    /// <summary>The wire value <c>married_not_filing_same_household</c>.</summary>
    [JsonStringEnumMemberName("married_not_filing_same_household")]
    MarriedNotFilingSameHousehold,

    /// <summary>The wire value <c>married_not_filing_separated</c>.</summary>
    [JsonStringEnumMemberName("married_not_filing_separated")]
    MarriedNotFilingSeparated,
}

/// <summary>Which B122A-2 line-33 bucket a secured payment belongs to (Python's <c>SECURED_PAYMENT_BUCKETS</c>).</summary>
public enum SecuredPaymentBucket
{
    /// <summary>The wire value <c>home</c>.</summary>
    [JsonStringEnumMemberName("home")]
    Home,

    /// <summary>The wire value <c>vehicle_1</c>.</summary>
    [JsonStringEnumMemberName("vehicle_1")]
    Vehicle1,

    /// <summary>The wire value <c>vehicle_2</c>.</summary>
    [JsonStringEnumMemberName("vehicle_2")]
    Vehicle2,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>B122A-1's income column: A is Debtor 1, B is Debtor 2 or the non-filing spouse (Python's <c>INCOME_COLUMNS</c>).</summary>
public enum IncomeColumn
{
    /// <summary>The wire value <c>A</c>.</summary>
    [JsonStringEnumMemberName("A")]
    A,

    /// <summary>The wire value <c>B</c>.</summary>
    [JsonStringEnumMemberName("B")]
    B,
}

/// <summary>A B122A-1 income line an override replaces (Python's <c>INCOME_LINE_CATEGORIES</c>).</summary>
public enum IncomeLineCategory
{
    /// <summary>The wire value <c>wages</c>.</summary>
    [JsonStringEnumMemberName("wages")]
    Wages,

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

    /// <summary>The wire value <c>unemployment_as_ssa</c>.</summary>
    [JsonStringEnumMemberName("unemployment_as_ssa")]
    UnemploymentAsSsa,
}
