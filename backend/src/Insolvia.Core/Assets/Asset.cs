using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Assets;

/// <summary>
/// One Schedule A/B asset (from <c>assets.py</c>). The vehicle members
/// (<see cref="Year"/>, <see cref="Make"/>, <see cref="Model"/>,
/// <see cref="Mileage"/>) and <see cref="PropertyTypes"/> mean something only
/// for their categories, but that is a completeness fact, not a shape rule.
/// </summary>
public sealed record AssetBody(
    AssetCategory? Category,
    ValueCollection<PropertyType> PropertyTypes,
    string? Description,
    string? County,
    decimal? ValueEntire,
    decimal? ValuePortionOwned,
    DebtorAttribution? OwnershipInterest,
    string? OwnershipInterestDescription,
    bool? CommunityProperty,
    string? Detail,
    int? Year,
    string? Make,
    string? Model,
    int? Mileage,
    bool? IncludesPersonalInformation);

/// <summary>The asset collection.</summary>
public static class AssetRules
{
    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<AssetBody> Kind = new("asset", "assets", "ASSET", Parse);

    private const int MaxVehicleYear = 2100;
    private const int MaxMileage = 999_999;

    /// <summary>Validates an asset body (Python's <c>parse_asset</c>).</summary>
    public static AssetBody Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new AssetBody(
            Category: FieldParsers.Choice<AssetCategory>(payload["category"], "category", errors),
            PropertyTypes: FieldParsers.ChoiceList<PropertyType>(payload["property_types"], "property_types", errors),
            Description: FieldParsers.Narrative(payload["description"], "description", errors),
            County: FieldParsers.Text(payload["county"], "county", errors),
            ValueEntire: FieldParsers.Money(payload["value_entire"], "value_entire", errors),
            ValuePortionOwned: FieldParsers.Money(payload["value_portion_owned"], "value_portion_owned", errors),
            OwnershipInterest: FieldParsers.Choice<DebtorAttribution>(payload["ownership_interest"], "ownership_interest", errors),
            OwnershipInterestDescription: FieldParsers.Text(payload["ownership_interest_description"], "ownership_interest_description", errors, limit: 500),
            CommunityProperty: FieldParsers.Boolean(payload["community_property"], "community_property", errors),
            Detail: FieldParsers.Narrative(payload["detail"], "detail", errors),
            Year: FieldParsers.WholeNumber(payload["year"], "year", errors, maximum: MaxVehicleYear),
            Make: FieldParsers.Text(payload["make"], "make", errors, limit: 60),
            Model: FieldParsers.Text(payload["model"], "model", errors, limit: 60),
            Mileage: FieldParsers.WholeNumber(payload["mileage"], "mileage", errors, maximum: MaxMileage),
            IncludesPersonalInformation: FieldParsers.Boolean(payload["includes_personal_information"], "includes_personal_information", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }
}

/// <summary>A Schedule A/B line's subject (Python's <c>ASSET_CATEGORIES</c>), named for what the line asks about.</summary>
public enum AssetCategory
{
    /// <summary>The wire value <c>real_property</c>.</summary>
    [JsonStringEnumMemberName("real_property")]
    RealProperty,

    /// <summary>The wire value <c>vehicle</c>.</summary>
    [JsonStringEnumMemberName("vehicle")]
    Vehicle,

    /// <summary>The wire value <c>watercraft_aircraft_or_recreational_vehicle</c>.</summary>
    [JsonStringEnumMemberName("watercraft_aircraft_or_recreational_vehicle")]
    WatercraftAircraftOrRecreationalVehicle,

    /// <summary>The wire value <c>household_goods</c>.</summary>
    [JsonStringEnumMemberName("household_goods")]
    HouseholdGoods,

    /// <summary>The wire value <c>electronics</c>.</summary>
    [JsonStringEnumMemberName("electronics")]
    Electronics,

    /// <summary>The wire value <c>collectibles</c>.</summary>
    [JsonStringEnumMemberName("collectibles")]
    Collectibles,

    /// <summary>The wire value <c>sports_and_hobby_equipment</c>.</summary>
    [JsonStringEnumMemberName("sports_and_hobby_equipment")]
    SportsAndHobbyEquipment,

    /// <summary>The wire value <c>firearms</c>.</summary>
    [JsonStringEnumMemberName("firearms")]
    Firearms,

    /// <summary>The wire value <c>clothes</c>.</summary>
    [JsonStringEnumMemberName("clothes")]
    Clothes,

    /// <summary>The wire value <c>jewelry</c>.</summary>
    [JsonStringEnumMemberName("jewelry")]
    Jewelry,

    /// <summary>The wire value <c>non_farm_animals</c>.</summary>
    [JsonStringEnumMemberName("non_farm_animals")]
    NonFarmAnimals,

    /// <summary>The wire value <c>other_personal_or_household</c>.</summary>
    [JsonStringEnumMemberName("other_personal_or_household")]
    OtherPersonalOrHousehold,

    /// <summary>The wire value <c>cash</c>.</summary>
    [JsonStringEnumMemberName("cash")]
    Cash,

    /// <summary>The wire value <c>deposits_of_money</c>.</summary>
    [JsonStringEnumMemberName("deposits_of_money")]
    DepositsOfMoney,

    /// <summary>The wire value <c>bonds_and_mutual_funds</c>.</summary>
    [JsonStringEnumMemberName("bonds_and_mutual_funds")]
    BondsAndMutualFunds,

    /// <summary>The wire value <c>non_publicly_traded_stock_and_business_interests</c>.</summary>
    [JsonStringEnumMemberName("non_publicly_traded_stock_and_business_interests")]
    NonPubliclyTradedStockAndBusinessInterests,

    /// <summary>The wire value <c>government_and_corporate_bonds</c>.</summary>
    [JsonStringEnumMemberName("government_and_corporate_bonds")]
    GovernmentAndCorporateBonds,

    /// <summary>The wire value <c>retirement_accounts</c>.</summary>
    [JsonStringEnumMemberName("retirement_accounts")]
    RetirementAccounts,

    /// <summary>The wire value <c>security_deposits_and_prepayments</c>.</summary>
    [JsonStringEnumMemberName("security_deposits_and_prepayments")]
    SecurityDepositsAndPrepayments,

    /// <summary>The wire value <c>annuities</c>.</summary>
    [JsonStringEnumMemberName("annuities")]
    Annuities,

    /// <summary>The wire value <c>education_accounts</c>.</summary>
    [JsonStringEnumMemberName("education_accounts")]
    EducationAccounts,

    /// <summary>The wire value <c>trusts_and_future_interests</c>.</summary>
    [JsonStringEnumMemberName("trusts_and_future_interests")]
    TrustsAndFutureInterests,

    /// <summary>The wire value <c>intellectual_property</c>.</summary>
    [JsonStringEnumMemberName("intellectual_property")]
    IntellectualProperty,

    /// <summary>The wire value <c>licenses_and_franchises</c>.</summary>
    [JsonStringEnumMemberName("licenses_and_franchises")]
    LicensesAndFranchises,

    /// <summary>The wire value <c>money_owed_to_you</c>.</summary>
    [JsonStringEnumMemberName("money_owed_to_you")]
    MoneyOwedToYou,

    /// <summary>The wire value <c>family_support_owed</c>.</summary>
    [JsonStringEnumMemberName("family_support_owed")]
    FamilySupportOwed,

    /// <summary>The wire value <c>other_amounts_owed</c>.</summary>
    [JsonStringEnumMemberName("other_amounts_owed")]
    OtherAmountsOwed,

    /// <summary>The wire value <c>insurance_policy_interests</c>.</summary>
    [JsonStringEnumMemberName("insurance_policy_interests")]
    InsurancePolicyInterests,

    /// <summary>The wire value <c>property_due_from_a_death</c>.</summary>
    [JsonStringEnumMemberName("property_due_from_a_death")]
    PropertyDueFromADeath,

    /// <summary>The wire value <c>claims_against_third_parties</c>.</summary>
    [JsonStringEnumMemberName("claims_against_third_parties")]
    ClaimsAgainstThirdParties,

    /// <summary>The wire value <c>other_contingent_and_unliquidated_claims</c>.</summary>
    [JsonStringEnumMemberName("other_contingent_and_unliquidated_claims")]
    OtherContingentAndUnliquidatedClaims,

    /// <summary>The wire value <c>other_financial_assets</c>.</summary>
    [JsonStringEnumMemberName("other_financial_assets")]
    OtherFinancialAssets,

    /// <summary>The wire value <c>accounts_receivable</c>.</summary>
    [JsonStringEnumMemberName("accounts_receivable")]
    AccountsReceivable,

    /// <summary>The wire value <c>office_equipment</c>.</summary>
    [JsonStringEnumMemberName("office_equipment")]
    OfficeEquipment,

    /// <summary>The wire value <c>machinery_and_tools_of_trade</c>.</summary>
    [JsonStringEnumMemberName("machinery_and_tools_of_trade")]
    MachineryAndToolsOfTrade,

    /// <summary>The wire value <c>inventory</c>.</summary>
    [JsonStringEnumMemberName("inventory")]
    Inventory,

    /// <summary>The wire value <c>partnership_and_joint_venture_interests</c>.</summary>
    [JsonStringEnumMemberName("partnership_and_joint_venture_interests")]
    PartnershipAndJointVentureInterests,

    /// <summary>The wire value <c>customer_lists_and_intangibles</c>.</summary>
    [JsonStringEnumMemberName("customer_lists_and_intangibles")]
    CustomerListsAndIntangibles,

    /// <summary>The wire value <c>other_business_property</c>.</summary>
    [JsonStringEnumMemberName("other_business_property")]
    OtherBusinessProperty,

    /// <summary>The wire value <c>farm_animals</c>.</summary>
    [JsonStringEnumMemberName("farm_animals")]
    FarmAnimals,

    /// <summary>The wire value <c>crops</c>.</summary>
    [JsonStringEnumMemberName("crops")]
    Crops,

    /// <summary>The wire value <c>farm_and_fishing_equipment</c>.</summary>
    [JsonStringEnumMemberName("farm_and_fishing_equipment")]
    FarmAndFishingEquipment,

    /// <summary>The wire value <c>farm_and_fishing_supplies</c>.</summary>
    [JsonStringEnumMemberName("farm_and_fishing_supplies")]
    FarmAndFishingSupplies,

    /// <summary>The wire value <c>other_farm_property</c>.</summary>
    [JsonStringEnumMemberName("other_farm_property")]
    OtherFarmProperty,

    /// <summary>The wire value <c>other_property_not_listed</c>.</summary>
    [JsonStringEnumMemberName("other_property_not_listed")]
    OtherPropertyNotListed,
}

/// <summary>106A/B line 1's "what is the property" checkboxes (Python's <c>PROPERTY_TYPES</c>).</summary>
public enum PropertyType
{
    /// <summary>The wire value <c>single_family_home</c>.</summary>
    [JsonStringEnumMemberName("single_family_home")]
    SingleFamilyHome,

    /// <summary>The wire value <c>duplex_or_multi_unit</c>.</summary>
    [JsonStringEnumMemberName("duplex_or_multi_unit")]
    DuplexOrMultiUnit,

    /// <summary>The wire value <c>condominium_or_cooperative</c>.</summary>
    [JsonStringEnumMemberName("condominium_or_cooperative")]
    CondominiumOrCooperative,

    /// <summary>The wire value <c>manufactured_or_mobile_home</c>.</summary>
    [JsonStringEnumMemberName("manufactured_or_mobile_home")]
    ManufacturedOrMobileHome,

    /// <summary>The wire value <c>land</c>.</summary>
    [JsonStringEnumMemberName("land")]
    Land,

    /// <summary>The wire value <c>investment_property</c>.</summary>
    [JsonStringEnumMemberName("investment_property")]
    InvestmentProperty,

    /// <summary>The wire value <c>timeshare</c>.</summary>
    [JsonStringEnumMemberName("timeshare")]
    Timeshare,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}
