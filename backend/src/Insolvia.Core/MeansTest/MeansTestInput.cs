using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.MeansTest;

// From means_test_inputs.py: B122A-2's (and B122C's) ENTERED figures, one
// record per case — the gate owns the cardinality. Everything derived is the
// calculator's, never stored.

/// <summary>One line-3 row: a part of the non-filing spouse's income not paid for the household.</summary>
public sealed record MaritalAdjustmentItem(string Id, string? Description, decimal? Amount);

/// <summary>One B122C-2 line-43 row: a special circumstance and its monthly amount.</summary>
public sealed record SpecialCircumstanceItem(string Id, string? Description, decimal? Amount);

/// <summary>One line-33d row: a secured debt beyond the home and the two claimed vehicles.</summary>
public sealed record OtherSecuredPayment(
    string Id,
    string? CreditorName,
    string? PropertyDescription,
    decimal? MonthlyPayment,
    string? ClaimId,
    SecuredPaymentBucket? Bucket,
    decimal? CureTotal);

/// <summary>One entered B122A-1 income line, replacing what the dated records derive for that column.</summary>
public sealed record IncomeLineOverride(string Id, IncomeColumn? Column, IncomeLineCategory? Category, decimal? MonthlyAmount);

/// <summary>B122A-2's entered figures, named for their subjects; money is monthly unless the name says otherwise.</summary>
public sealed record MeansTestInputBody(
    [property: JsonPropertyName("people_under_65")] int? PeopleUnder65,
    [property: JsonPropertyName("people_65_or_older")] int? People65OrOlder,
    int? MedianHouseholdSize,
    int? IrsFamilySize,
    int? IrsHousingFamilySize,
    MaritalFilingStatus? MaritalFilingStatus,
    bool? NonConsumerDebts,
    bool? DisabledVeteran,
    bool? ReservistNationalGuard,
    ValueCollection<IncomeLineOverride> IncomeOverrides,
    ValueCollection<MaritalAdjustmentItem> MaritalAdjustments,
    decimal? HomeSecuredMonthlyTotal,
    decimal? HousingAdjustmentAmount,
    string? HousingAdjustmentExplanation,
    int? VehicleCount,
    [property: JsonPropertyName("vehicle_1_loan_monthly")] decimal? Vehicle1LoanMonthly,
    [property: JsonPropertyName("vehicle_2_loan_monthly")] decimal? Vehicle2LoanMonthly,
    decimal? AdditionalPublicTransportation,
    decimal? Taxes,
    decimal? InvoluntaryDeductions,
    decimal? TermLifeInsurance,
    decimal? CourtOrderedPayments,
    decimal? EducationForEmploymentOrDisability,
    decimal? Childcare,
    decimal? HealthcareAboveAllowance,
    decimal? OptionalTelecom,
    decimal? HealthInsurance,
    decimal? DisabilityInsurance,
    decimal? HealthSavingsAccount,
    decimal? FamilyCareContributions,
    decimal? FamilyViolenceProtection,
    decimal? HomeEnergyExcess,
    [property: JsonPropertyName("education_under_18")] decimal? EducationUnder18,
    decimal? AdditionalFoodClothing,
    decimal? CharitableContributions,
    ValueCollection<OtherSecuredPayment> OtherSecuredPayments,
    decimal? PriorityCureTotal,
    [property: JsonPropertyName("ch13_eligible")] bool? Ch13Eligible,
    [property: JsonPropertyName("ch13_projected_plan_payment")] decimal? Ch13ProjectedPlanPayment,
    bool? CommitmentPeriodMaritalAdjustment,
    decimal? ChildSupportForDependents,
    decimal? QualifiedRetirementDeductions,
    ValueCollection<SpecialCircumstanceItem> SpecialCircumstances);

/// <summary>The means-test-input collection.</summary>
public static class MeansTestInputRules
{
    /// <summary>The Local Standards publish figures for at most two vehicles.</summary>
    public const int MaxClaimedVehicles = 2;

    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<MeansTestInputBody> Kind = new("means_test_input", "means_test_inputs", "MEANS_TEST_INPUT", Parse);

    /// <summary>Validates a means-test-input body (Python's <c>parse_means_test_input</c>).</summary>
    public static MeansTestInputBody Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        decimal? Amount(string key) => FieldParsers.Money(payload[key], key, errors);

        var vehicleCount = FieldParsers.WholeNumber(payload["vehicle_count"], "vehicle_count", errors);
        if (vehicleCount is > MaxClaimedVehicles)
        {
            errors["vehicle_count"] = "The Local Standards publish figures for at most two vehicles — the form's own \"2 or more\".";
        }

        var body = new MeansTestInputBody(
            PeopleUnder65: FieldParsers.WholeNumber(payload["people_under_65"], "people_under_65", errors),
            People65OrOlder: FieldParsers.WholeNumber(payload["people_65_or_older"], "people_65_or_older", errors),
            MedianHouseholdSize: FieldParsers.WholeNumber(payload["median_household_size"], "median_household_size", errors),
            IrsFamilySize: FieldParsers.WholeNumber(payload["irs_family_size"], "irs_family_size", errors),
            IrsHousingFamilySize: FieldParsers.WholeNumber(payload["irs_housing_family_size"], "irs_housing_family_size", errors),
            MaritalFilingStatus: FieldParsers.Choice<MaritalFilingStatus>(payload["marital_filing_status"], "marital_filing_status", errors),
            NonConsumerDebts: FieldParsers.Boolean(payload["non_consumer_debts"], "non_consumer_debts", errors),
            DisabledVeteran: FieldParsers.Boolean(payload["disabled_veteran"], "disabled_veteran", errors),
            ReservistNationalGuard: FieldParsers.Boolean(payload["reservist_national_guard"], "reservist_national_guard", errors),
            IncomeOverrides: ParseIncomeOverrides(payload["income_overrides"], errors),
            MaritalAdjustments: IdRows.Parse(
                payload["marital_adjustments"],
                "marital_adjustments",
                errors,
                (id, raw, path) => new MaritalAdjustmentItem(
                    id,
                    FieldParsers.Text(raw["description"], $"{path}.description", errors),
                    FieldParsers.Money(raw["amount"], $"{path}.amount", errors))),
            HomeSecuredMonthlyTotal: Amount("home_secured_monthly_total"),
            HousingAdjustmentAmount: Amount("housing_adjustment_amount"),
            HousingAdjustmentExplanation: FieldParsers.Text(payload["housing_adjustment_explanation"], "housing_adjustment_explanation", errors),
            VehicleCount: vehicleCount,
            Vehicle1LoanMonthly: Amount("vehicle_1_loan_monthly"),
            Vehicle2LoanMonthly: Amount("vehicle_2_loan_monthly"),
            AdditionalPublicTransportation: Amount("additional_public_transportation"),
            Taxes: Amount("taxes"),
            InvoluntaryDeductions: Amount("involuntary_deductions"),
            TermLifeInsurance: Amount("term_life_insurance"),
            CourtOrderedPayments: Amount("court_ordered_payments"),
            EducationForEmploymentOrDisability: Amount("education_for_employment_or_disability"),
            Childcare: Amount("childcare"),
            HealthcareAboveAllowance: Amount("healthcare_above_allowance"),
            OptionalTelecom: Amount("optional_telecom"),
            HealthInsurance: Amount("health_insurance"),
            DisabilityInsurance: Amount("disability_insurance"),
            HealthSavingsAccount: Amount("health_savings_account"),
            FamilyCareContributions: Amount("family_care_contributions"),
            FamilyViolenceProtection: Amount("family_violence_protection"),
            HomeEnergyExcess: Amount("home_energy_excess"),
            EducationUnder18: Amount("education_under_18"),
            AdditionalFoodClothing: Amount("additional_food_clothing"),
            CharitableContributions: Amount("charitable_contributions"),
            OtherSecuredPayments: IdRows.Parse(
                payload["other_secured_payments"],
                "other_secured_payments",
                errors,
                (id, raw, path) => new OtherSecuredPayment(
                    id,
                    FieldParsers.Text(raw["creditor_name"], $"{path}.creditor_name", errors),
                    FieldParsers.Text(raw["property_description"], $"{path}.property_description", errors),
                    FieldParsers.Money(raw["monthly_payment"], $"{path}.monthly_payment", errors),
                    FieldParsers.Text(raw["claim_id"], $"{path}.claim_id", errors, limit: 64),
                    FieldParsers.Choice<SecuredPaymentBucket>(raw["bucket"], $"{path}.bucket", errors),
                    FieldParsers.Money(raw["cure_total"], $"{path}.cure_total", errors))),
            PriorityCureTotal: Amount("priority_cure_total"),
            Ch13Eligible: FieldParsers.Boolean(payload["ch13_eligible"], "ch13_eligible", errors),
            Ch13ProjectedPlanPayment: Amount("ch13_projected_plan_payment"),
            CommitmentPeriodMaritalAdjustment: FieldParsers.Boolean(payload["commitment_period_marital_adjustment"], "commitment_period_marital_adjustment", errors),
            ChildSupportForDependents: Amount("child_support_for_dependents"),
            QualifiedRetirementDeductions: Amount("qualified_retirement_deductions"),
            SpecialCircumstances: IdRows.Parse(
                payload["special_circumstances"],
                "special_circumstances",
                errors,
                (id, raw, path) => new SpecialCircumstanceItem(
                    id,
                    FieldParsers.Text(raw["description"], $"{path}.description", errors),
                    FieldParsers.Money(raw["amount"], $"{path}.amount", errors))));

        foreach (var (name, size) in new[]
        {
            ("median_household_size", body.MedianHouseholdSize),
            ("irs_family_size", body.IrsFamilySize),
            ("irs_housing_family_size", body.IrsHousingFamilySize),
        })
        {
            if (size is < 1)
            {
                errors[name] = "A household has at least one person.";
            }
        }

        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>
    /// The entered income lines: one override per (column, line) — a second
    /// for the same line is refused under its category.
    /// </summary>
    private static ValueCollection<IncomeLineOverride> ParseIncomeOverrides(JsonNode? value, Dictionary<string, string> errors)
    {
        var seenLines = new HashSet<(IncomeColumn, IncomeLineCategory)>();
        var parsed = new List<IncomeLineOverride>();
        _ = IdRows.Parse(
            value,
            "income_overrides",
            errors,
            (id, raw, path) =>
            {
                var item = new IncomeLineOverride(
                    id,
                    FieldParsers.Choice<IncomeColumn>(raw["column"], $"{path}.column", errors),
                    FieldParsers.Choice<IncomeLineCategory>(raw["category"], $"{path}.category", errors),
                    FieldParsers.Money(raw["monthly_amount"], $"{path}.monthly_amount", errors));
                if (item.Column is { } column && item.Category is { } category && !seenLines.Add((column, category)))
                {
                    errors[$"{path}.category"] = "This column already has an override for that line.";
                    return item;
                }

                parsed.Add(item);
                return item;
            });
        return parsed.ToValueCollection();
    }
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
