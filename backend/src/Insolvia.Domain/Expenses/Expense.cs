using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Expenses;

/// <summary>106J's household header — one per household (from <c>expenses.py</c>).</summary>
public sealed record HouseholdBody(
    WhichHousehold? WhichHousehold = null,
    bool? SeparateHousehold = null,
    bool? ExpensesIncludeOthers = null,
    bool? ChangeExpected = null,
    [property: Narrative] string? ChangeExplanation = null);

/// <summary>One 106J expense line, in its household.</summary>
public sealed record ExpenseBody(
    [property: Text(64)] string? HouseholdId = null,
    ExpenseCategory? Category = null,
    [property: Text] string? SpecifyText = null,
    [property: Money] decimal? Amount = null);

/// <summary>
/// One 106J line-2 dependent. NO NAME: the form does not ask for one, so the
/// record has no member to hold it — a name sent is dropped, never stored.
/// </summary>
public sealed record DependentBody(
    [property: Text(64)] string? HouseholdId = null,
    [property: Text] string? Relationship = null,
    [property: Count(150)] int? Age = null,
    bool? LivesWithDebtor = null);

/// <summary>The three 106J collections.</summary>
public static class ExpenseRules
{
    /// <summary>The household collection's kind.</summary>
    public static readonly EntityKind<HouseholdBody> Household = new("household", "households", "HOUSEHOLD");

    /// <summary>The expense collection's kind.</summary>
    public static readonly EntityKind<ExpenseBody> Expense = new("expense", "expenses", "EXPENSE");

    /// <summary>The dependent collection's kind.</summary>
    public static readonly EntityKind<DependentBody> Dependent = new("dependent", "dependents", "DEPENDENT");
}

/// <summary>106J's household: the main one, or Debtor 2's separate one (Python's <c>WHICH_HOUSEHOLDS</c>).</summary>
public enum WhichHousehold
{
    /// <summary>The wire value <c>main</c>.</summary>
    [JsonStringEnumMemberName("main")]
    Main,

    /// <summary>The wire value <c>debtor_2_separate</c>.</summary>
    [JsonStringEnumMemberName("debtor_2_separate")]
    Debtor2Separate,
}

/// <summary>106J's expense lines, named for the subject (Python's <c>EXPENSE_CATEGORIES</c>).</summary>
public enum ExpenseCategory
{
    /// <summary>The wire value <c>rent_or_home_ownership</c>.</summary>
    [JsonStringEnumMemberName("rent_or_home_ownership")]
    RentOrHomeOwnership,

    /// <summary>The wire value <c>real_estate_taxes</c>.</summary>
    [JsonStringEnumMemberName("real_estate_taxes")]
    RealEstateTaxes,

    /// <summary>The wire value <c>property_insurance</c>.</summary>
    [JsonStringEnumMemberName("property_insurance")]
    PropertyInsurance,

    /// <summary>The wire value <c>home_maintenance</c>.</summary>
    [JsonStringEnumMemberName("home_maintenance")]
    HomeMaintenance,

    /// <summary>The wire value <c>homeowners_association_dues</c>.</summary>
    [JsonStringEnumMemberName("homeowners_association_dues")]
    HomeownersAssociationDues,

    /// <summary>The wire value <c>additional_mortgage_payments</c>.</summary>
    [JsonStringEnumMemberName("additional_mortgage_payments")]
    AdditionalMortgagePayments,

    /// <summary>The wire value <c>electricity_heat_gas</c>.</summary>
    [JsonStringEnumMemberName("electricity_heat_gas")]
    ElectricityHeatGas,

    /// <summary>The wire value <c>water_sewer_garbage</c>.</summary>
    [JsonStringEnumMemberName("water_sewer_garbage")]
    WaterSewerGarbage,

    /// <summary>The wire value <c>telephone_and_internet</c>.</summary>
    [JsonStringEnumMemberName("telephone_and_internet")]
    TelephoneAndInternet,

    /// <summary>The wire value <c>other_utilities</c>.</summary>
    [JsonStringEnumMemberName("other_utilities")]
    OtherUtilities,

    /// <summary>The wire value <c>food_and_housekeeping</c>.</summary>
    [JsonStringEnumMemberName("food_and_housekeeping")]
    FoodAndHousekeeping,

    /// <summary>The wire value <c>childcare_and_education</c>.</summary>
    [JsonStringEnumMemberName("childcare_and_education")]
    ChildcareAndEducation,

    /// <summary>The wire value <c>clothing_and_laundry</c>.</summary>
    [JsonStringEnumMemberName("clothing_and_laundry")]
    ClothingAndLaundry,

    /// <summary>The wire value <c>personal_care</c>.</summary>
    [JsonStringEnumMemberName("personal_care")]
    PersonalCare,

    /// <summary>The wire value <c>medical_and_dental</c>.</summary>
    [JsonStringEnumMemberName("medical_and_dental")]
    MedicalAndDental,

    /// <summary>The wire value <c>transportation</c>.</summary>
    [JsonStringEnumMemberName("transportation")]
    Transportation,

    /// <summary>The wire value <c>entertainment_and_recreation</c>.</summary>
    [JsonStringEnumMemberName("entertainment_and_recreation")]
    EntertainmentAndRecreation,

    /// <summary>The wire value <c>charitable_contributions</c>.</summary>
    [JsonStringEnumMemberName("charitable_contributions")]
    CharitableContributions,

    /// <summary>The wire value <c>life_insurance</c>.</summary>
    [JsonStringEnumMemberName("life_insurance")]
    LifeInsurance,

    /// <summary>The wire value <c>health_insurance</c>.</summary>
    [JsonStringEnumMemberName("health_insurance")]
    HealthInsurance,

    /// <summary>The wire value <c>vehicle_insurance</c>.</summary>
    [JsonStringEnumMemberName("vehicle_insurance")]
    VehicleInsurance,

    /// <summary>The wire value <c>other_insurance</c>.</summary>
    [JsonStringEnumMemberName("other_insurance")]
    OtherInsurance,

    /// <summary>The wire value <c>taxes</c>.</summary>
    [JsonStringEnumMemberName("taxes")]
    Taxes,

    /// <summary>The wire value <c>vehicle_installment_payments</c>.</summary>
    [JsonStringEnumMemberName("vehicle_installment_payments")]
    VehicleInstallmentPayments,

    /// <summary>The wire value <c>other_installment_payments</c>.</summary>
    [JsonStringEnumMemberName("other_installment_payments")]
    OtherInstallmentPayments,

    /// <summary>The wire value <c>alimony_and_support_payments</c>.</summary>
    [JsonStringEnumMemberName("alimony_and_support_payments")]
    AlimonyAndSupportPayments,

    /// <summary>The wire value <c>support_of_others</c>.</summary>
    [JsonStringEnumMemberName("support_of_others")]
    SupportOfOthers,

    /// <summary>The wire value <c>other_property_mortgages</c>.</summary>
    [JsonStringEnumMemberName("other_property_mortgages")]
    OtherPropertyMortgages,

    /// <summary>The wire value <c>other_property_taxes</c>.</summary>
    [JsonStringEnumMemberName("other_property_taxes")]
    OtherPropertyTaxes,

    /// <summary>The wire value <c>other_property_insurance</c>.</summary>
    [JsonStringEnumMemberName("other_property_insurance")]
    OtherPropertyInsurance,

    /// <summary>The wire value <c>other_property_maintenance</c>.</summary>
    [JsonStringEnumMemberName("other_property_maintenance")]
    OtherPropertyMaintenance,

    /// <summary>The wire value <c>other_property_association_dues</c>.</summary>
    [JsonStringEnumMemberName("other_property_association_dues")]
    OtherPropertyAssociationDues,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}
