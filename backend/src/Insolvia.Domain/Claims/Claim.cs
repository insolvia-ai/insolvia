using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Claims;

/// <summary>
/// One debt — a 106D/E/F row (from <c>claims.py</c>). It names its creditor
/// by id (the creditor is the deduplicated name-and-address); the
/// class-specific members (lien, collateral, priority) mean something only
/// for their class, which is a completeness fact, not a shape rule.
/// </summary>
public sealed record ClaimBody(
    [property: Text(64)] string? CreditorId = null,
    ClaimClass? ClaimClass = null,
    [property: Text(4), RegularExpression("^[0-9]*$", ErrorMessage = Claims.NoticeParties.Last4Message)] string? AccountLast4 = null,
    DateOnly? DateIncurred = null,
    [property: Money] decimal? Amount = null,
    bool? Contingent = null,
    bool? Unliquidated = null,
    bool? Disputed = null,
    bool? SubjectToOffset = null,
    DebtorAttribution? WhoIncurred = null,
    bool? CommunityDebt = null,
    ValueCollection<NoticeParty>? NoticeParties = null,
    [property: Text(64)] string? AssetId = null,
    [property: Count(ClaimRules.MaxLienPosition)] int? LienPosition = null,
    [property: Text(500)] string? CollateralDescription = null,
    [property: Money] decimal? CollateralValue = null,
    ValueCollection<LienNature>? LienNature = null,
    [property: Text] string? LienNatureOther = null,
    [property: Money] decimal? UnsecuredAmountOverride = null,
    ClaimIntention? Intention = null,
    [property: Text(500)] string? IntentionExplanation = null,
    [property: Money] decimal? PriorityAmount = null,
    [property: Money] decimal? NonpriorityAmount = null,
    PriorityType? PriorityType = null,
    [property: Text] string? PriorityTypeOther = null,
    NonpriorityType? NonpriorityType = null,
    [property: Text] string? NonpriorityTypeOther = null) : IValidatableObject
{
    /// <summary>Who else to notify about the debt — collection agencies, attorneys.</summary>
    [Rows]
    public ValueCollection<NoticeParty> NoticeParties { get; init; } = NoticeParties ?? [];

    /// <summary>106D's "nature of lien" checkboxes.</summary>
    public ValueCollection<LienNature> LienNature { get; init; } = LienNature ?? [];

    /// <summary>
    /// 1 is the senior lien. Zero is refused rather than read as "first": two
    /// counting conventions on one asset would order the liens wrongly
    /// without any field being malformed.
    /// </summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (LienPosition == 0)
        {
            yield return new ValidationResult("Must be 1 or more — 1 is the senior lien.", [nameof(LienPosition)]);
        }
    }
}

/// <summary>The claim collection.</summary>
public static class ClaimRules
{
    /// <summary>The highest lien position a claim may hold.</summary>
    public const int MaxLienPosition = 99;

    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<ClaimBody> Kind = new("claim", "claims", "CLAIM");
}

/// <summary>Which schedule a claim belongs on (Python's <c>CLAIM_CLASSES</c>).</summary>
public enum ClaimClass
{
    /// <summary>The wire value <c>secured</c>.</summary>
    [JsonStringEnumMemberName("secured")]
    Secured,

    /// <summary>The wire value <c>priority_unsecured</c>.</summary>
    [JsonStringEnumMemberName("priority_unsecured")]
    PriorityUnsecured,

    /// <summary>The wire value <c>nonpriority_unsecured</c>.</summary>
    [JsonStringEnumMemberName("nonpriority_unsecured")]
    NonpriorityUnsecured,
}

/// <summary>106D's "nature of lien" checkboxes (Python's <c>LIEN_NATURES</c>).</summary>
public enum LienNature
{
    /// <summary>The wire value <c>agreement</c>.</summary>
    [JsonStringEnumMemberName("agreement")]
    Agreement,

    /// <summary>The wire value <c>statutory</c>.</summary>
    [JsonStringEnumMemberName("statutory")]
    Statutory,

    /// <summary>The wire value <c>judgment</c>.</summary>
    [JsonStringEnumMemberName("judgment")]
    Judgment,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>106E's priority claim types (Python's <c>PRIORITY_TYPES</c>).</summary>
public enum PriorityType
{
    /// <summary>The wire value <c>domestic_support</c>.</summary>
    [JsonStringEnumMemberName("domestic_support")]
    DomesticSupport,

    /// <summary>The wire value <c>tax_and_government</c>.</summary>
    [JsonStringEnumMemberName("tax_and_government")]
    TaxAndGovernment,

    /// <summary>The wire value <c>death_or_injury_while_intoxicated</c>.</summary>
    [JsonStringEnumMemberName("death_or_injury_while_intoxicated")]
    DeathOrInjuryWhileIntoxicated,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>106F's nonpriority claim types (Python's <c>NONPRIORITY_TYPES</c>).</summary>
public enum NonpriorityType
{
    /// <summary>The wire value <c>student_loan</c>.</summary>
    [JsonStringEnumMemberName("student_loan")]
    StudentLoan,

    /// <summary>The wire value <c>separation_or_divorce</c>.</summary>
    [JsonStringEnumMemberName("separation_or_divorce")]
    SeparationOrDivorce,

    /// <summary>The wire value <c>pension_or_profit_sharing</c>.</summary>
    [JsonStringEnumMemberName("pension_or_profit_sharing")]
    PensionOrProfitSharing,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>Form 108's intention for secured property (Python's <c>INTENTIONS</c>).</summary>
public enum ClaimIntention
{
    /// <summary>The wire value <c>surrender</c>.</summary>
    [JsonStringEnumMemberName("surrender")]
    Surrender,

    /// <summary>The wire value <c>retain_redeem</c>.</summary>
    [JsonStringEnumMemberName("retain_redeem")]
    RetainRedeem,

    /// <summary>The wire value <c>retain_reaffirm</c>.</summary>
    [JsonStringEnumMemberName("retain_reaffirm")]
    RetainReaffirm,

    /// <summary>The wire value <c>retain_other</c>.</summary>
    [JsonStringEnumMemberName("retain_other")]
    RetainOther,
}
