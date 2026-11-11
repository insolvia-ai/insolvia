using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Claims;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Sofa;

// From sofa.py: the Statement of Financial Affairs (Form 107) — one generic
// collection, one entry per answered row, whose `payload` is the question's
// own shape, named by its `entryType`. Error paths are keyed `payload.<field>`.

/// <summary>A named party and their address — the shape most Form 107 rows open with.</summary>
public sealed record Party([property: Text] string? Name = null, Address? Address = null)
{
    /// <summary>The party's address; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new();
}

/// <summary>
/// One answer's question-specific shape, named by its <c>entryType</c>. Not
/// abstract on purpose: a payload without an <c>entryType</c> reads as this
/// base record, which <see cref="SofaEntryBody"/> refuses by name, rather
/// than failing the whole read.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "entryType")]
[JsonDerivedType(typeof(MaritalStatus), "marital_status")]
[JsonDerivedType(typeof(PriorAddress), "prior_address")]
[JsonDerivedType(typeof(CommunityPropertyResidence), "community_property_residence")]
[JsonDerivedType(typeof(IncomeByPeriod), "income_by_period")]
[JsonDerivedType(typeof(ConsumerDebtDeclaration), "consumer_debt_declaration")]
[JsonDerivedType(typeof(CreditorPayment), "creditor_payment")]
[JsonDerivedType(typeof(InsiderPayment), "insider_payment")]
[JsonDerivedType(typeof(InsiderBenefitPayment), "insider_benefit_payment")]
[JsonDerivedType(typeof(Lawsuit), "lawsuit")]
[JsonDerivedType(typeof(Repossession), "repossession")]
[JsonDerivedType(typeof(Setoff), "setoff")]
[JsonDerivedType(typeof(Receivership), "receivership")]
[JsonDerivedType(typeof(Gift), "gift")]
[JsonDerivedType(typeof(CharitableContribution), "charitable_contribution")]
[JsonDerivedType(typeof(Loss), "loss")]
[JsonDerivedType(typeof(ConsultantPayment), "consultant_payment")]
[JsonDerivedType(typeof(CreditorAssistancePayment), "creditor_assistance_payment")]
[JsonDerivedType(typeof(PropertyTransfer), "property_transfer")]
[JsonDerivedType(typeof(SelfSettledTrust), "self_settled_trust")]
[JsonDerivedType(typeof(ClosedAccount), "closed_account")]
[JsonDerivedType(typeof(SafeDepositBox), "safe_deposit_box")]
[JsonDerivedType(typeof(StorageUnit), "storage_unit")]
[JsonDerivedType(typeof(HeldForAnother), "held_for_another")]
[JsonDerivedType(typeof(EnvironmentalNotice), "environmental_notice")]
[JsonDerivedType(typeof(EnvironmentalProceeding), "environmental_proceeding")]
[JsonDerivedType(typeof(BusinessConnection), "business_connection")]
[JsonDerivedType(typeof(FinancialStatementIssued), "financial_statement_issued")]
public record SofaPayload;

/// <summary>Line 2: where the debtor lived in the last three years.</summary>
public sealed record PriorAddress(SofaDebtor? WhichDebtor = null, Address? Address = null, DateOnly? FromDate = null, DateOnly? ToDate = null) : SofaPayload
{
    /// <summary>The address lived at.</summary>
    public Address Address { get; init; } = Address ?? new();
}

/// <summary>Lines 4-5: income by period.</summary>
public sealed record IncomeByPeriod(
    SofaDebtor? WhichDebtor = null,
    IncomeKind? Kind = null,
    [property: Text] string? Description = null,
    DateOnly? PeriodStart = null,
    DateOnly? PeriodEnd = null,
    [property: Money] decimal? GrossAmount = null) : SofaPayload;

/// <summary>Line 6: payments to creditors.</summary>
public sealed record CreditorPayment(
    Party? Creditor = null,
    ValueCollection<DateOnly>? Dates = null,
    [property: Money] decimal? TotalPaid = null,
    [property: Money] decimal? AmountStillOwed = null,
    ValueCollection<PaymentPurpose>? PaymentFor = null,
    [property: Text] string? PaymentForOther = null) : SofaPayload
{
    /// <summary>Who was paid.</summary>
    public Party Creditor { get; init; } = Creditor ?? new();

    /// <summary>When.</summary>
    public ValueCollection<DateOnly> Dates { get; init; } = Dates ?? [];

    /// <summary>What the payment was for.</summary>
    public ValueCollection<PaymentPurpose> PaymentFor { get; init; } = PaymentFor ?? [];
}

/// <summary>Line 7: payments to insiders.</summary>
public sealed record InsiderPayment(
    Party? Insider = null,
    [property: Text] string? Relationship = null,
    ValueCollection<DateOnly>? Dates = null,
    [property: Money] decimal? TotalPaid = null,
    [property: Money] decimal? AmountStillOwed = null,
    [property: Narrative] string? Reason = null) : SofaPayload
{
    /// <summary>Who was paid.</summary>
    public Party Insider { get; init; } = Insider ?? new();

    /// <summary>When.</summary>
    public ValueCollection<DateOnly> Dates { get; init; } = Dates ?? [];
}

/// <summary>Line 8: payments that benefited an insider.</summary>
public sealed record InsiderBenefitPayment(
    Party? Recipient = null,
    [property: Text] string? InsiderName = null,
    ValueCollection<DateOnly>? Dates = null,
    [property: Money] decimal? TotalPaid = null,
    [property: Narrative] string? Reason = null) : SofaPayload
{
    /// <summary>Who was paid.</summary>
    public Party Recipient { get; init; } = Recipient ?? new();

    /// <summary>When.</summary>
    public ValueCollection<DateOnly> Dates { get; init; } = Dates ?? [];
}

/// <summary>Line 9: a lawsuit the debtor was a party to.</summary>
public sealed record Lawsuit(
    [property: Text] string? CaseTitle = null,
    [property: Text(64)] string? CaseNumber = null,
    [property: Text] string? NatureOfCase = null,
    Party? Court = null,
    LegalStatus? Status = null) : SofaPayload
{
    /// <summary>The court.</summary>
    public Party Court { get; init; } = Court ?? new();
}

/// <summary>Line 10: property repossessed, foreclosed, garnished or attached.</summary>
public sealed record Repossession(
    Party? Creditor = null,
    RepossessionAction? Action = null,
    [property: Narrative] string? Description = null,
    DateOnly? Date = null,
    [property: Money] decimal? Value = null) : SofaPayload
{
    /// <summary>The creditor who took it.</summary>
    public Party Creditor { get; init; } = Creditor ?? new();
}

/// <summary>Line 11: a setoff.</summary>
public sealed record Setoff(
    Party? Creditor = null,
    [property: Narrative] string? Description = null,
    DateOnly? Date = null,
    [property: Money] decimal? Amount = null) : SofaPayload
{
    /// <summary>The creditor who set off.</summary>
    public Party Creditor { get; init; } = Creditor ?? new();
}

/// <summary>Line 12: property held by a receiver or custodian.</summary>
public sealed record Receivership(
    Party? Custodian = null,
    [property: Narrative] string? Description = null,
    [property: Money] decimal? Value = null,
    [property: Text] string? CaseTitle = null,
    [property: Text(64)] string? CaseNumber = null,
    Party? Court = null,
    DateOnly? Date = null) : SofaPayload
{
    /// <summary>The custodian.</summary>
    public Party Custodian { get; init; } = Custodian ?? new();

    /// <summary>The court.</summary>
    public Party Court { get; init; } = Court ?? new();
}

/// <summary>Line 13: gifts over the threshold.</summary>
public sealed record Gift(
    Party? Recipient = null,
    [property: Text] string? Relationship = null,
    [property: Narrative] string? Description = null,
    ValueCollection<DateOnly>? Dates = null,
    [property: Money] decimal? Value = null) : SofaPayload
{
    /// <summary>Who received it.</summary>
    public Party Recipient { get; init; } = Recipient ?? new();

    /// <summary>When.</summary>
    public ValueCollection<DateOnly> Dates { get; init; } = Dates ?? [];
}

/// <summary>Line 14: charitable contributions over the threshold.</summary>
public sealed record CharitableContribution(
    Party? Organization = null,
    [property: Narrative] string? Description = null,
    ValueCollection<DateOnly>? Dates = null,
    [property: Money] decimal? Value = null) : SofaPayload
{
    /// <summary>The charity.</summary>
    public Party Organization { get; init; } = Organization ?? new();

    /// <summary>When.</summary>
    public ValueCollection<DateOnly> Dates { get; init; } = Dates ?? [];
}

/// <summary>Line 15: a loss from fire, theft, disaster or gambling.</summary>
public sealed record Loss(
    [property: Narrative] string? Description = null,
    [property: Narrative] string? InsuranceCoverage = null,
    DateOnly? Date = null,
    [property: Money] decimal? Value = null) : SofaPayload;

/// <summary>Line 16: a payment for bankruptcy advice or preparation.</summary>
public sealed record ConsultantPayment(
    Party? Person = null,
    [property: Text] string? EmailOrWebsite = null,
    [property: Text] string? WhoMadePayment = null,
    [property: Narrative] string? Description = null,
    DateOnly? Date = null,
    [property: Money] decimal? Amount = null) : SofaPayload
{
    /// <summary>Who was paid.</summary>
    public Party Person { get; init; } = Person ?? new();
}

/// <summary>Line 17: a payment to someone who promised to help with creditors.</summary>
public sealed record CreditorAssistancePayment(
    Party? Person = null,
    [property: Narrative] string? Description = null,
    DateOnly? Date = null,
    [property: Money] decimal? Amount = null) : SofaPayload
{
    /// <summary>Who was paid.</summary>
    public Party Person { get; init; } = Person ?? new();
}

/// <summary>Line 18: a transfer of property outside the ordinary course.</summary>
public sealed record PropertyTransfer(
    Party? Transferee = null,
    [property: Text] string? Relationship = null,
    [property: Narrative] string? Description = null,
    [property: Narrative] string? ValueReceived = null,
    DateOnly? Date = null) : SofaPayload
{
    /// <summary>Who received it.</summary>
    public Party Transferee { get; init; } = Transferee ?? new();
}

/// <summary>Line 19: a self-settled trust.</summary>
public sealed record SelfSettledTrust(
    [property: Text] string? TrustName = null,
    [property: Narrative] string? Description = null,
    DateOnly? Date = null) : SofaPayload;

/// <summary>Line 20: a closed financial account.</summary>
public sealed record ClosedAccount(
    Party? Institution = null,
    [property: Text(4), RegularExpression("^[0-9]*$", ErrorMessage = NoticeParties.Last4Message)] string? AccountLast4 = null,
    AccountType? AccountType = null,
    DateOnly? DateClosed = null,
    [property: Money] decimal? LastBalance = null) : SofaPayload
{
    /// <summary>The institution.</summary>
    public Party Institution { get; init; } = Institution ?? new();
}

/// <summary>Line 21: a safe deposit box.</summary>
public sealed record SafeDepositBox(
    Party? Institution = null,
    ValueCollection<string>? WhoHasAccess = null,
    [property: Narrative] string? Description = null,
    bool? StillHave = null) : SofaPayload
{
    /// <summary>The institution.</summary>
    public Party Institution { get; init; } = Institution ?? new();

    /// <summary>Who else can open it.</summary>
    [Text]
    public ValueCollection<string> WhoHasAccess { get; init; } = WhoHasAccess ?? [];
}

/// <summary>Line 22: a storage unit.</summary>
public sealed record StorageUnit(
    Party? Facility = null,
    ValueCollection<string>? WhoHasAccess = null,
    [property: Narrative] string? Description = null,
    bool? StillHave = null) : SofaPayload
{
    /// <summary>The facility.</summary>
    public Party Facility { get; init; } = Facility ?? new();

    /// <summary>Who else can open it.</summary>
    [Text]
    public ValueCollection<string> WhoHasAccess { get; init; } = WhoHasAccess ?? [];
}

/// <summary>Line 23: property held for someone else.</summary>
public sealed record HeldForAnother(
    Party? Owner = null,
    [property: Narrative] string? Location = null,
    [property: Narrative] string? Description = null,
    [property: Money] decimal? Value = null) : SofaPayload
{
    /// <summary>Whose it is.</summary>
    public Party Owner { get; init; } = Owner ?? new();
}

/// <summary>Lines 24-25: an environmental notice received or release reported.</summary>
public sealed record EnvironmentalNotice(
    EnvironmentalNoticeKind? Kind = null,
    Party? Site = null,
    Party? GovernmentalUnit = null,
    [property: Text] string? EnvironmentalLaw = null,
    DateOnly? Date = null) : SofaPayload
{
    /// <summary>The site.</summary>
    public Party Site { get; init; } = Site ?? new();

    /// <summary>The governmental unit.</summary>
    public Party GovernmentalUnit { get; init; } = GovernmentalUnit ?? new();
}

/// <summary>Line 26: an environmental proceeding.</summary>
public sealed record EnvironmentalProceeding(
    [property: Text] string? CaseTitle = null,
    [property: Text(64)] string? CaseNumber = null,
    Party? Court = null,
    [property: Text] string? NatureOfCase = null,
    LegalStatus? Status = null) : SofaPayload
{
    /// <summary>The court.</summary>
    public Party Court { get; init; } = Court ?? new();
}

/// <summary>Line 27: a business connection.</summary>
public sealed record BusinessConnection(
    Party? Business = null,
    [property: Text] string? NatureOfBusiness = null,
    [property: Text(20)] string? Ein = null,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    ValueCollection<BusinessConnectionKind>? Connection = null) : SofaPayload
{
    /// <summary>The business.</summary>
    public Party Business { get; init; } = Business ?? new();

    /// <summary>How the debtor was connected.</summary>
    public ValueCollection<BusinessConnectionKind> Connection { get; init; } = Connection ?? [];
}

/// <summary>Line 28: a financial statement issued.</summary>
public sealed record FinancialStatementIssued(Party? Recipient = null, DateOnly? DateIssued = null) : SofaPayload
{
    /// <summary>Who received it.</summary>
    public Party Recipient { get; init; } = Recipient ?? new();
}

/// <summary>Line 1: marital status.</summary>
public sealed record MaritalStatus(MaritalStatusKind? Status = null) : SofaPayload;

/// <summary>Line 3: a community-property state lived in.</summary>
public sealed record CommunityPropertyResidence([property: Text(40)] string? State = null) : SofaPayload;

/// <summary>The consumer-debt declaration.</summary>
public sealed record ConsumerDebtDeclaration(bool? PrimarilyConsumerDebts = null) : SofaPayload;

/// <summary>One Form 107 entry: its answer, whose <c>entryType</c> names the question.</summary>
public sealed record SofaEntryBody(SofaPayload? Payload = null) : IValidatableObject
{
    /// <summary>A payload names its question.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Payload is not null && Payload.GetType() == typeof(SofaPayload))
        {
            yield return new ValidationResult("Required — it names the question the payload answers.", ["payload.entryType"]);
        }
    }
}

/// <summary>The SOFA collection.</summary>
public static class SofaRules
{
    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<SofaEntryBody> Kind = new("sofa_entry", "sofa_entries", "SOFA_ENTRY");
}

/// <summary>Which debtor a Form 107 row is about (Python's <c>SOFA_DEBTORS</c>).</summary>
public enum SofaDebtor
{
    /// <summary>The wire value <c>debtor_1</c>.</summary>
    [JsonStringEnumMemberName("debtor_1")]
    Debtor1,

    /// <summary>The wire value <c>debtor_2</c>.</summary>
    [JsonStringEnumMemberName("debtor_2")]
    Debtor2,

    /// <summary>The wire value <c>both</c>.</summary>
    [JsonStringEnumMemberName("both")]
    Both,
}

/// <summary>A legal action's status (Python's <c>LEGAL_STATUSES</c>).</summary>
public enum LegalStatus
{
    /// <summary>The wire value <c>pending</c>.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>The wire value <c>on_appeal</c>.</summary>
    [JsonStringEnumMemberName("on_appeal")]
    OnAppeal,

    /// <summary>The wire value <c>concluded</c>.</summary>
    [JsonStringEnumMemberName("concluded")]
    Concluded,
}

/// <summary>Form 107 Part 2's income kinds (Python's <c>INCOME_KINDS</c>).</summary>
public enum IncomeKind
{
    /// <summary>The wire value <c>wages_and_commissions</c>.</summary>
    [JsonStringEnumMemberName("wages_and_commissions")]
    WagesAndCommissions,

    /// <summary>The wire value <c>operating_a_business</c>.</summary>
    [JsonStringEnumMemberName("operating_a_business")]
    OperatingABusiness,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>Form 107 line 6's "was this payment for" checkboxes (Python's <c>PAYMENT_PURPOSES</c>).</summary>
public enum PaymentPurpose
{
    /// <summary>The wire value <c>mortgage</c>.</summary>
    [JsonStringEnumMemberName("mortgage")]
    Mortgage,

    /// <summary>The wire value <c>car</c>.</summary>
    [JsonStringEnumMemberName("car")]
    Car,

    /// <summary>The wire value <c>credit_card</c>.</summary>
    [JsonStringEnumMemberName("credit_card")]
    CreditCard,

    /// <summary>The wire value <c>loan_repayment</c>.</summary>
    [JsonStringEnumMemberName("loan_repayment")]
    LoanRepayment,

    /// <summary>The wire value <c>suppliers_or_vendors</c>.</summary>
    [JsonStringEnumMemberName("suppliers_or_vendors")]
    SuppliersOrVendors,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>Form 107 line 10: what happened to the property (Python's <c>REPOSSESSION_ACTIONS</c>).</summary>
public enum RepossessionAction
{
    /// <summary>The wire value <c>repossessed</c>.</summary>
    [JsonStringEnumMemberName("repossessed")]
    Repossessed,

    /// <summary>The wire value <c>foreclosed</c>.</summary>
    [JsonStringEnumMemberName("foreclosed")]
    Foreclosed,

    /// <summary>The wire value <c>garnished</c>.</summary>
    [JsonStringEnumMemberName("garnished")]
    Garnished,

    /// <summary>The wire value <c>attached</c>.</summary>
    [JsonStringEnumMemberName("attached")]
    Attached,
}

/// <summary>Form 107 line 20's account types (Python's <c>ACCOUNT_TYPES</c>).</summary>
public enum AccountType
{
    /// <summary>The wire value <c>checking</c>.</summary>
    [JsonStringEnumMemberName("checking")]
    Checking,

    /// <summary>The wire value <c>savings</c>.</summary>
    [JsonStringEnumMemberName("savings")]
    Savings,

    /// <summary>The wire value <c>money_market</c>.</summary>
    [JsonStringEnumMemberName("money_market")]
    MoneyMarket,

    /// <summary>The wire value <c>brokerage</c>.</summary>
    [JsonStringEnumMemberName("brokerage")]
    Brokerage,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>Form 107 lines 24-25: a liability notice received, or a release reported (Python's <c>ENVIRONMENTAL_NOTICE_KINDS</c>).</summary>
public enum EnvironmentalNoticeKind
{
    /// <summary>The wire value <c>liability_notice_received</c>.</summary>
    [JsonStringEnumMemberName("liability_notice_received")]
    LiabilityNoticeReceived,

    /// <summary>The wire value <c>release_reported</c>.</summary>
    [JsonStringEnumMemberName("release_reported")]
    ReleaseReported,
}

/// <summary>Form 107 line 27's connection checkboxes (Python's <c>BUSINESS_CONNECTIONS</c>).</summary>
public enum BusinessConnectionKind
{
    /// <summary>The wire value <c>sole_proprietor</c>.</summary>
    [JsonStringEnumMemberName("sole_proprietor")]
    SoleProprietor,

    /// <summary>The wire value <c>llc_member</c>.</summary>
    [JsonStringEnumMemberName("llc_member")]
    LlcMember,

    /// <summary>The wire value <c>partner</c>.</summary>
    [JsonStringEnumMemberName("partner")]
    Partner,

    /// <summary>The wire value <c>officer_or_director</c>.</summary>
    [JsonStringEnumMemberName("officer_or_director")]
    OfficerOrDirector,

    /// <summary>The wire value <c>owner_of_5_percent</c>.</summary>
    [JsonStringEnumMemberName("owner_of_5_percent")]
    OwnerOf5Percent,
}

/// <summary>Form 107 line 1's marital status (Python's <c>MARITAL_STATUSES</c>).</summary>
public enum MaritalStatusKind
{
    /// <summary>The wire value <c>married</c>.</summary>
    [JsonStringEnumMemberName("married")]
    Married,

    /// <summary>The wire value <c>not_married</c>.</summary>
    [JsonStringEnumMemberName("not_married")]
    NotMarried,
}
