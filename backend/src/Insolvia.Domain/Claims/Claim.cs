using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Claims;

/// <summary>
/// One debt — a 106D/E/F row (from <c>claims.py</c>). It names its creditor
/// by id (the creditor is the deduplicated name-and-address); the
/// class-specific members (lien, collateral, priority) mean something only
/// for their class, which is a completeness fact, not a shape rule.
/// </summary>
public sealed record ClaimBody(
    string? CreditorId,
    ClaimClass? ClaimClass,
    [property: JsonPropertyName("account_last4")] string? AccountLast4,
    DateOnly? DateIncurred,
    decimal? Amount,
    bool? Contingent,
    bool? Unliquidated,
    bool? Disputed,
    bool? SubjectToOffset,
    DebtorAttribution? WhoIncurred,
    bool? CommunityDebt,
    ValueCollection<NoticeParty> NoticeParties,
    string? AssetId,
    int? LienPosition,
    string? CollateralDescription,
    decimal? CollateralValue,
    ValueCollection<LienNature> LienNature,
    string? LienNatureOther,
    decimal? UnsecuredAmountOverride,
    ClaimIntention? Intention,
    string? IntentionExplanation,
    decimal? PriorityAmount,
    decimal? NonpriorityAmount,
    PriorityType? PriorityType,
    string? PriorityTypeOther,
    NonpriorityType? NonpriorityType,
    string? NonpriorityTypeOther);

/// <summary>The claim collection.</summary>
public static class ClaimRules
{
    /// <summary>The highest lien position a claim may hold.</summary>
    public const int MaxLienPosition = 99;

    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<ClaimBody> Kind = new("claim", "claims", "CLAIM", Parse);

    /// <summary>Validates a claim body (Python's <c>parse_claim</c>).</summary>
    public static ClaimBody Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new ClaimBody(
            CreditorId: FieldParsers.Text(payload["creditor_id"], "creditor_id", errors, limit: 64),
            ClaimClass: FieldParsers.Choice<ClaimClass>(payload["claim_class"], "claim_class", errors),
            AccountLast4: Claims.NoticeParties.Last4(payload["account_last4"], "account_last4", errors),
            DateIncurred: FieldParsers.FormDate(payload["date_incurred"], "date_incurred", errors),
            Amount: FieldParsers.Money(payload["amount"], "amount", errors),
            Contingent: FieldParsers.Boolean(payload["contingent"], "contingent", errors),
            Unliquidated: FieldParsers.Boolean(payload["unliquidated"], "unliquidated", errors),
            Disputed: FieldParsers.Boolean(payload["disputed"], "disputed", errors),
            SubjectToOffset: FieldParsers.Boolean(payload["subject_to_offset"], "subject_to_offset", errors),
            WhoIncurred: FieldParsers.Choice<DebtorAttribution>(payload["who_incurred"], "who_incurred", errors),
            CommunityDebt: FieldParsers.Boolean(payload["community_debt"], "community_debt", errors),
            NoticeParties: Claims.NoticeParties.Parse(payload["notice_parties"], errors),
            AssetId: FieldParsers.Text(payload["asset_id"], "asset_id", errors, limit: 64),
            LienPosition: ParseLienPosition(payload["lien_position"], errors),
            CollateralDescription: FieldParsers.Text(payload["collateral_description"], "collateral_description", errors, limit: 500),
            CollateralValue: FieldParsers.Money(payload["collateral_value"], "collateral_value", errors),
            LienNature: FieldParsers.ChoiceList<LienNature>(payload["lien_nature"], "lien_nature", errors),
            LienNatureOther: FieldParsers.Text(payload["lien_nature_other"], "lien_nature_other", errors),
            UnsecuredAmountOverride: FieldParsers.Money(payload["unsecured_amount_override"], "unsecured_amount_override", errors),
            Intention: FieldParsers.Choice<ClaimIntention>(payload["intention"], "intention", errors),
            IntentionExplanation: FieldParsers.Text(payload["intention_explanation"], "intention_explanation", errors, limit: 500),
            PriorityAmount: FieldParsers.Money(payload["priority_amount"], "priority_amount", errors),
            NonpriorityAmount: FieldParsers.Money(payload["nonpriority_amount"], "nonpriority_amount", errors),
            PriorityType: FieldParsers.Choice<PriorityType>(payload["priority_type"], "priority_type", errors),
            PriorityTypeOther: FieldParsers.Text(payload["priority_type_other"], "priority_type_other", errors),
            NonpriorityType: FieldParsers.Choice<NonpriorityType>(payload["nonpriority_type"], "nonpriority_type", errors),
            NonpriorityTypeOther: FieldParsers.Text(payload["nonpriority_type_other"], "nonpriority_type_other", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>
    /// 1 is the senior lien. Zero is refused rather than read as "first": two
    /// counting conventions on one asset would order the liens wrongly
    /// without any field being malformed.
    /// </summary>
    private static int? ParseLienPosition(JsonNode? value, Dictionary<string, string> errors)
    {
        var position = FieldParsers.WholeNumber(value, "lien_position", errors, maximum: MaxLienPosition);
        if (position == 0)
        {
            errors["lien_position"] = "Must be 1 or more — 1 is the senior lien.";
            return null;
        }

        return position;
    }
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
