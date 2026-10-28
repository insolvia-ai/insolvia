using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Petitions;

// From petitions.py: B101's entities (issue #93) — the petition's answers,
// its repeating rows, and the Part 7 signer block.

/// <summary>B101 line 14: property needing immediate attention.</summary>
public sealed record HazardousProperty(string? Description = null, string? WhyImmediate = null, Address? Address = null)
{
    /// <summary>Where the property is.</summary>
    public Address Address { get; init; } = Address ?? new Address();
}

/// <summary>The petition's own answers — one per case.</summary>
public sealed record PetitionBody(
    FeeHandling? FeeHandling,
    bool? RentsResidence,
    bool? EvictionJudgmentAgainstYou,
    SmallBusinessStatus? SmallBusinessStatus,
    HazardousProperty HazardousProperty,
    DebtCharacter? DebtCharacter,
    string? DebtCharacterOther,
    [property: JsonPropertyName("ch7_funds_available_for_creditors")] bool? Ch7FundsAvailableForCreditors,
    EstimatedCreditors? EstimatedCreditors,
    EstimatedDollars? EstimatedAssets,
    EstimatedDollars? EstimatedLiabilities,
    DateOnly? ExpectedFilingDate);

/// <summary>B101 line 9: a bankruptcy filed within the last 8 years.</summary>
public sealed record PriorCaseBody(string? District, DateOnly? FiledOn, string? CaseNumber);

/// <summary>B101 line 10: a pending case by a spouse, partner, or affiliate.</summary>
public sealed record RelatedCaseBody(string? DebtorName, string? Relationship, string? District, DateOnly? FiledOn, string? CaseNumber);

/// <summary>B101 line 12: a business the debtor operates as a sole proprietor.</summary>
public sealed record SoleProprietorshipBody(string? Name, Address Address, BusinessType? BusinessType);

/// <summary>
/// B101 Part 7's attorney or petition-preparer block, and B2030's § 329(a)
/// compensation disclosure — facts about THIS engagement. The balance due is
/// the difference of the two amounts and is never stored.
/// </summary>
public sealed record FilingProfessionalBody(
    FilingProfessionalRole? Role,
    PersonName Name,
    string? FirmName,
    Address Address,
    string? Phone,
    string? Email,
    string? BarNumber,
    string? BarState,
    DateOnly? SignatureDate,
    decimal? CompensationAgreed,
    decimal? CompensationReceived,
    CompensationSource? CompensationSourcePaid,
    string? CompensationSourcePaidOther,
    CompensationSource? CompensationSourceToBePaid,
    string? CompensationSourceToBePaidOther,
    bool? CompensationShared,
    string? ServicesOther,
    string? ServicesExcluded);

/// <summary>The five B101 collections.</summary>
public static class PetitionRules
{
    /// <summary>The petition collection's kind.</summary>
    public static readonly EntityKind<PetitionBody> Petition = new("petition", "petitions", "PETITION", ParsePetition);

    /// <summary>The prior-case collection's kind.</summary>
    public static readonly EntityKind<PriorCaseBody> PriorCase = new("prior_case", "prior_cases", "PRIOR_CASE", ParsePriorCase);

    /// <summary>The related-case collection's kind.</summary>
    public static readonly EntityKind<RelatedCaseBody> RelatedCase = new("related_case", "related_cases", "RELATED_CASE", ParseRelatedCase);

    /// <summary>The sole-proprietorship collection's kind.</summary>
    public static readonly EntityKind<SoleProprietorshipBody> SoleProprietorship = new("sole_proprietorship", "sole_proprietorships", "SOLE_PROPRIETORSHIP", ParseSoleProprietorship);

    /// <summary>The filing-professional collection's kind.</summary>
    public static readonly EntityKind<FilingProfessionalBody> FilingProfessional = new("filing_professional", "filing_professionals", "FILING_PROFESSIONAL", ParseFilingProfessional);

    /// <summary>Validates a petition body (Python's <c>parse_petition</c>).</summary>
    public static PetitionBody ParsePetition(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new PetitionBody(
            FeeHandling: FieldParsers.Choice<FeeHandling>(payload["fee_handling"], "fee_handling", errors),
            RentsResidence: FieldParsers.Boolean(payload["rents_residence"], "rents_residence", errors),
            EvictionJudgmentAgainstYou: FieldParsers.Boolean(payload["eviction_judgment_against_you"], "eviction_judgment_against_you", errors),
            SmallBusinessStatus: FieldParsers.Choice<SmallBusinessStatus>(payload["small_business_status"], "small_business_status", errors),
            HazardousProperty: ParseHazardousProperty(payload["hazardous_property"], "hazardous_property", errors),
            DebtCharacter: FieldParsers.Choice<DebtCharacter>(payload["debt_character"], "debt_character", errors),
            DebtCharacterOther: FieldParsers.Text(payload["debt_character_other"], "debt_character_other", errors),
            Ch7FundsAvailableForCreditors: FieldParsers.Boolean(payload["ch7_funds_available_for_creditors"], "ch7_funds_available_for_creditors", errors),
            EstimatedCreditors: FieldParsers.Choice<EstimatedCreditors>(payload["estimated_creditors"], "estimated_creditors", errors),
            EstimatedAssets: FieldParsers.Choice<EstimatedDollars>(payload["estimated_assets"], "estimated_assets", errors),
            EstimatedLiabilities: FieldParsers.Choice<EstimatedDollars>(payload["estimated_liabilities"], "estimated_liabilities", errors),
            ExpectedFilingDate: FieldParsers.FormDate(payload["expected_filing_date"], "expected_filing_date", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>Validates a prior-case body (Python's <c>parse_prior_case</c>).</summary>
    public static PriorCaseBody ParsePriorCase(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new PriorCaseBody(
            District: FieldParsers.Text(payload["district"], "district", errors),
            FiledOn: FieldParsers.FormDate(payload["filed_on"], "filed_on", errors),
            CaseNumber: FieldParsers.Text(payload["case_number"], "case_number", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>Validates a related-case body (Python's <c>parse_related_case</c>).</summary>
    public static RelatedCaseBody ParseRelatedCase(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new RelatedCaseBody(
            DebtorName: FieldParsers.Text(payload["debtor_name"], "debtor_name", errors),
            Relationship: FieldParsers.Text(payload["relationship"], "relationship", errors),
            District: FieldParsers.Text(payload["district"], "district", errors),
            FiledOn: FieldParsers.FormDate(payload["filed_on"], "filed_on", errors),
            CaseNumber: FieldParsers.Text(payload["case_number"], "case_number", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>Validates a sole-proprietorship body (Python's <c>parse_sole_proprietorship</c>).</summary>
    public static SoleProprietorshipBody ParseSoleProprietorship(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new SoleProprietorshipBody(
            Name: FieldParsers.Text(payload["name"], "name", errors),
            Address: FieldParsers.ParseAddress(payload["address"], "address", errors),
            BusinessType: FieldParsers.Choice<BusinessType>(payload["business_type"], "business_type", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>Validates a filing-professional body (Python's <c>parse_filing_professional</c>).</summary>
    public static FilingProfessionalBody ParseFilingProfessional(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new FilingProfessionalBody(
            Role: FieldParsers.Choice<FilingProfessionalRole>(payload["role"], "role", errors),
            Name: FieldParsers.ParseName(payload["name"], "name", errors),
            FirmName: FieldParsers.Text(payload["firm_name"], "firm_name", errors),
            Address: FieldParsers.ParseAddress(payload["address"], "address", errors),
            Phone: FieldParsers.Text(payload["phone"], "phone", errors, limit: 32),
            Email: FieldParsers.Text(payload["email"], "email", errors),
            BarNumber: FieldParsers.Text(payload["bar_number"], "bar_number", errors, limit: 32),
            BarState: FieldParsers.Text(payload["bar_state"], "bar_state", errors, limit: 2),
            SignatureDate: FieldParsers.FormDate(payload["signature_date"], "signature_date", errors),
            CompensationAgreed: FieldParsers.Money(payload["compensation_agreed"], "compensation_agreed", errors),
            CompensationReceived: FieldParsers.Money(payload["compensation_received"], "compensation_received", errors),
            CompensationSourcePaid: FieldParsers.Choice<CompensationSource>(payload["compensation_source_paid"], "compensation_source_paid", errors),
            CompensationSourcePaidOther: FieldParsers.Text(payload["compensation_source_paid_other"], "compensation_source_paid_other", errors),
            CompensationSourceToBePaid: FieldParsers.Choice<CompensationSource>(payload["compensation_source_to_be_paid"], "compensation_source_to_be_paid", errors),
            CompensationSourceToBePaidOther: FieldParsers.Text(payload["compensation_source_to_be_paid_other"], "compensation_source_to_be_paid_other", errors),
            CompensationShared: FieldParsers.Boolean(payload["compensation_shared"], "compensation_shared", errors),
            ServicesOther: FieldParsers.Narrative(payload["services_other"], "services_other", errors),
            ServicesExcluded: FieldParsers.Narrative(payload["services_excluded"], "services_excluded", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    private static HazardousProperty ParseHazardousProperty(JsonNode? value, string path, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return new HazardousProperty();
        }

        if (value is not JsonObject raw)
        {
            // Python's own lower-case spelling, kept: a client may match it.
            errors[path] = "must be an object";
            return new HazardousProperty();
        }

        return new HazardousProperty(
            Description: FieldParsers.Narrative(raw["description"], $"{path}.description", errors),
            WhyImmediate: FieldParsers.Narrative(raw["why_immediate"], $"{path}.why_immediate", errors),
            Address: FieldParsers.ParseAddress(raw["address"], $"{path}.address", errors));
    }
}

/// <summary>B101 line 8: how the filing fee is paid (Python's <c>FEE_HANDLING</c>).</summary>
public enum FeeHandling
{
    /// <summary>The wire value <c>full</c>.</summary>
    [JsonStringEnumMemberName("full")]
    Full,

    /// <summary>The wire value <c>installments</c>.</summary>
    [JsonStringEnumMemberName("installments")]
    Installments,

    /// <summary>The wire value <c>waiver</c>.</summary>
    [JsonStringEnumMemberName("waiver")]
    Waiver,
}

/// <summary>B101 line 12's business-type checkboxes (Python's <c>BUSINESS_TYPES</c>).</summary>
public enum BusinessType
{
    /// <summary>The wire value <c>health_care_business</c>.</summary>
    [JsonStringEnumMemberName("health_care_business")]
    HealthCareBusiness,

    /// <summary>The wire value <c>single_asset_real_estate</c>.</summary>
    [JsonStringEnumMemberName("single_asset_real_estate")]
    SingleAssetRealEstate,

    /// <summary>The wire value <c>stockbroker</c>.</summary>
    [JsonStringEnumMemberName("stockbroker")]
    Stockbroker,

    /// <summary>The wire value <c>commodity_broker</c>.</summary>
    [JsonStringEnumMemberName("commodity_broker")]
    CommodityBroker,

    /// <summary>The wire value <c>none_of_the_above</c>.</summary>
    [JsonStringEnumMemberName("none_of_the_above")]
    NoneOfTheAbove,
}

/// <summary>B101 line 13's Chapter 11 small-business answer (Python's <c>SMALL_BUSINESS_STATUSES</c>).</summary>
public enum SmallBusinessStatus
{
    /// <summary>The wire value <c>not_filing_under_chapter_11</c>.</summary>
    [JsonStringEnumMemberName("not_filing_under_chapter_11")]
    NotFilingUnderChapter11,

    /// <summary>The wire value <c>chapter_11_not_small_business</c>.</summary>
    [JsonStringEnumMemberName("chapter_11_not_small_business")]
    Chapter11NotSmallBusiness,

    /// <summary>The wire value <c>chapter_11_small_business</c>.</summary>
    [JsonStringEnumMemberName("chapter_11_small_business")]
    Chapter11SmallBusiness,

    /// <summary>The wire value <c>chapter_11_subchapter_v</c>.</summary>
    [JsonStringEnumMemberName("chapter_11_subchapter_v")]
    Chapter11SubchapterV,
}

/// <summary>B101 line 16: what kind of debts are primarily held (Python's <c>DEBT_CHARACTERS</c>).</summary>
public enum DebtCharacter
{
    /// <summary>The wire value <c>consumer</c>.</summary>
    [JsonStringEnumMemberName("consumer")]
    Consumer,

    /// <summary>The wire value <c>business</c>.</summary>
    [JsonStringEnumMemberName("business")]
    Business,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>B101 line 18's creditor-count bands (Python's <c>ESTIMATED_CREDITORS_BANDS</c>).</summary>
public enum EstimatedCreditors
{
    /// <summary>The wire value <c>1_49</c>.</summary>
    [JsonStringEnumMemberName("1_49")]
    Band1To49,

    /// <summary>The wire value <c>50_99</c>.</summary>
    [JsonStringEnumMemberName("50_99")]
    Band50To99,

    /// <summary>The wire value <c>100_199</c>.</summary>
    [JsonStringEnumMemberName("100_199")]
    Band100To199,

    /// <summary>The wire value <c>200_999</c>.</summary>
    [JsonStringEnumMemberName("200_999")]
    Band200To999,

    /// <summary>The wire value <c>1000_5000</c>.</summary>
    [JsonStringEnumMemberName("1000_5000")]
    Band1000To5000,

    /// <summary>The wire value <c>5001_10000</c>.</summary>
    [JsonStringEnumMemberName("5001_10000")]
    Band5001To10000,

    /// <summary>The wire value <c>10001_25000</c>.</summary>
    [JsonStringEnumMemberName("10001_25000")]
    Band10001To25000,

    /// <summary>The wire value <c>25001_50000</c>.</summary>
    [JsonStringEnumMemberName("25001_50000")]
    Band25001To50000,

    /// <summary>The wire value <c>50001_100000</c>.</summary>
    [JsonStringEnumMemberName("50001_100000")]
    Band50001To100000,

    /// <summary>The wire value <c>more_than_100000</c>.</summary>
    [JsonStringEnumMemberName("more_than_100000")]
    MoreThan100000,
}

/// <summary>B101 lines 19-20's dollar bands (Python's <c>ESTIMATED_DOLLAR_BANDS</c>).</summary>
public enum EstimatedDollars
{
    /// <summary>The wire value <c>0_50000</c>.</summary>
    [JsonStringEnumMemberName("0_50000")]
    Band0To50000,

    /// <summary>The wire value <c>50001_100000</c>.</summary>
    [JsonStringEnumMemberName("50001_100000")]
    Band50001To100000,

    /// <summary>The wire value <c>100001_500000</c>.</summary>
    [JsonStringEnumMemberName("100001_500000")]
    Band100001To500000,

    /// <summary>The wire value <c>500001_1000000</c>.</summary>
    [JsonStringEnumMemberName("500001_1000000")]
    Band500001To1000000,

    /// <summary>The wire value <c>1000001_10000000</c>.</summary>
    [JsonStringEnumMemberName("1000001_10000000")]
    Band1000001To10000000,

    /// <summary>The wire value <c>10000001_50000000</c>.</summary>
    [JsonStringEnumMemberName("10000001_50000000")]
    Band10000001To50000000,

    /// <summary>The wire value <c>50000001_100000000</c>.</summary>
    [JsonStringEnumMemberName("50000001_100000000")]
    Band50000001To100000000,

    /// <summary>The wire value <c>100000001_500000000</c>.</summary>
    [JsonStringEnumMemberName("100000001_500000000")]
    Band100000001To500000000,

    /// <summary>The wire value <c>500000001_1000000000</c>.</summary>
    [JsonStringEnumMemberName("500000001_1000000000")]
    Band500000001To1000000000,

    /// <summary>The wire value <c>1000000001_10000000000</c>.</summary>
    [JsonStringEnumMemberName("1000000001_10000000000")]
    Band1000000001To10000000000,

    /// <summary>The wire value <c>10000000001_50000000000</c>.</summary>
    [JsonStringEnumMemberName("10000000001_50000000000")]
    Band10000000001To50000000000,

    /// <summary>The wire value <c>more_than_50000000000</c>.</summary>
    [JsonStringEnumMemberName("more_than_50000000000")]
    MoreThan50000000000,
}

/// <summary>B101 Part 7's signer: an attorney or a petition preparer (Python's <c>FILING_PROFESSIONAL_ROLES</c>).</summary>
public enum FilingProfessionalRole
{
    /// <summary>The wire value <c>attorney</c>.</summary>
    [JsonStringEnumMemberName("attorney")]
    Attorney,

    /// <summary>The wire value <c>bankruptcy_petition_preparer</c>.</summary>
    [JsonStringEnumMemberName("bankruptcy_petition_preparer")]
    BankruptcyPetitionPreparer,
}

/// <summary>B2030's source of compensation (Python's <c>COMPENSATION_SOURCES</c>).</summary>
public enum CompensationSource
{
    /// <summary>The wire value <c>debtor</c>.</summary>
    [JsonStringEnumMemberName("debtor")]
    Debtor,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}
