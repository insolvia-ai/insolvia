using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Petitions;

// From petitions.py: B101's entities (issue #93) — the petition's answers,
// its repeating rows, and the Part 7 signer block.

/// <summary>B101 line 14: property needing immediate attention.</summary>
public sealed record HazardousProperty(
    [property: Narrative] string? Description = null,
    [property: Narrative] string? WhyImmediate = null,
    Address? Address = null)
{
    /// <summary>Where the property is.</summary>
    public Address Address { get; init; } = Address ?? new Address();
}

/// <summary>The petition's own answers — one per case.</summary>
public sealed record PetitionBody(
    FeeHandling? FeeHandling = null,
    bool? RentsResidence = null,
    bool? EvictionJudgmentAgainstYou = null,
    SmallBusinessStatus? SmallBusinessStatus = null,
    HazardousProperty? HazardousProperty = null,
    DebtCharacter? DebtCharacter = null,
    [property: Text] string? DebtCharacterOther = null,
    bool? Ch7FundsAvailableForCreditors = null,
    EstimatedCreditors? EstimatedCreditors = null,
    EstimatedDollars? EstimatedAssets = null,
    EstimatedDollars? EstimatedLiabilities = null,
    DateOnly? ExpectedFilingDate = null)
{
    /// <summary>B101 line 14; an empty one when none was given.</summary>
    public HazardousProperty HazardousProperty { get; init; } = HazardousProperty ?? new();
}

/// <summary>B101 line 9: a bankruptcy filed within the last 8 years.</summary>
public sealed record PriorCaseBody(
    [property: Text] string? District = null,
    DateOnly? FiledOn = null,
    [property: Text] string? CaseNumber = null);

/// <summary>B101 line 10: a pending case by a spouse, partner, or affiliate.</summary>
public sealed record RelatedCaseBody(
    [property: Text] string? DebtorName = null,
    [property: Text] string? Relationship = null,
    [property: Text] string? District = null,
    DateOnly? FiledOn = null,
    [property: Text] string? CaseNumber = null);

/// <summary>B101 line 12: a business the debtor operates as a sole proprietor.</summary>
public sealed record SoleProprietorshipBody(
    [property: Text] string? Name = null,
    Address? Address = null,
    BusinessType? BusinessType = null)
{
    /// <summary>The business's address; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new();
}

/// <summary>
/// B101 Part 7's attorney or petition-preparer block, and B2030's § 329(a)
/// compensation disclosure — facts about THIS engagement. The balance due is
/// the difference of the two amounts and is never stored.
/// </summary>
public sealed record FilingProfessionalBody(
    FilingProfessionalRole? Role = null,
    PersonName? Name = null,
    [property: Text] string? FirmName = null,
    Address? Address = null,
    [property: Text(32)] string? Phone = null,
    [property: Text] string? Email = null,
    [property: Text(32)] string? BarNumber = null,
    [property: Text(2)] string? BarState = null,
    DateOnly? SignatureDate = null,
    [property: Money] decimal? CompensationAgreed = null,
    [property: Money] decimal? CompensationReceived = null,
    CompensationSource? CompensationSourcePaid = null,
    [property: Text] string? CompensationSourcePaidOther = null,
    CompensationSource? CompensationSourceToBePaid = null,
    [property: Text] string? CompensationSourceToBePaidOther = null,
    bool? CompensationShared = null,
    [property: Narrative] string? ServicesOther = null,
    [property: Narrative] string? ServicesExcluded = null)
{
    /// <summary>The signer's name; an empty one when none was given.</summary>
    public PersonName Name { get; init; } = Name ?? new();

    /// <summary>The signer's address; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new();
}

/// <summary>The five B101 collections.</summary>
public static class PetitionRules
{
    /// <summary>The petition collection's kind.</summary>
    public static readonly EntityKind<PetitionBody> Petition = new("petition", "petitions", "PETITION");

    /// <summary>The prior-case collection's kind.</summary>
    public static readonly EntityKind<PriorCaseBody> PriorCase = new("prior_case", "prior_cases", "PRIOR_CASE");

    /// <summary>The related-case collection's kind.</summary>
    public static readonly EntityKind<RelatedCaseBody> RelatedCase = new("related_case", "related_cases", "RELATED_CASE");

    /// <summary>The sole-proprietorship collection's kind.</summary>
    public static readonly EntityKind<SoleProprietorshipBody> SoleProprietorship = new("sole_proprietorship", "sole_proprietorships", "SOLE_PROPRIETORSHIP");

    /// <summary>The filing-professional collection's kind.</summary>
    public static readonly EntityKind<FilingProfessionalBody> FilingProfessional = new("filing_professional", "filing_professionals", "FILING_PROFESSIONAL");
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
