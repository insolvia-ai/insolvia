using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Sofa;

// From sofa.py: the Statement of Financial Affairs (Form 107) — one generic
// collection, one entry per answered row, typed by `entry_type`, whose
// `payload` is the question's own shape. Error paths are keyed
// `payload.<field>`.

/// <summary>A named party and their address — the shape most Form 107 rows open with.</summary>
public sealed record Party(string? Name, Address Address);

/// <summary>One answer's question-specific shape. Serialized through its runtime type.</summary>
[JsonConverter(typeof(RuntimeTypeConverter<SofaPayload>))]
public abstract record SofaPayload;

/// <summary>Line 2: where the debtor lived in the last three years.</summary>
public sealed record PriorAddress(SofaDebtor? WhichDebtor, Address Address, DateOnly? FromDate, DateOnly? ToDate) : SofaPayload;

/// <summary>Lines 4-5: income by period.</summary>
public sealed record IncomeByPeriod(SofaDebtor? WhichDebtor, IncomeKind? Kind, string? Description, DateOnly? PeriodStart, DateOnly? PeriodEnd, decimal? GrossAmount) : SofaPayload;

/// <summary>Line 6: payments to creditors.</summary>
public sealed record CreditorPayment(Party Creditor, ValueCollection<DateOnly> Dates, decimal? TotalPaid, decimal? AmountStillOwed, ValueCollection<PaymentPurpose> PaymentFor, string? PaymentForOther) : SofaPayload;

/// <summary>Line 7: payments to insiders.</summary>
public sealed record InsiderPayment(Party Insider, string? Relationship, ValueCollection<DateOnly> Dates, decimal? TotalPaid, decimal? AmountStillOwed, string? Reason) : SofaPayload;

/// <summary>Line 8: payments that benefited an insider.</summary>
public sealed record InsiderBenefitPayment(Party Recipient, string? InsiderName, ValueCollection<DateOnly> Dates, decimal? TotalPaid, string? Reason) : SofaPayload;

/// <summary>Line 9: a lawsuit the debtor was a party to.</summary>
public sealed record Lawsuit(string? CaseTitle, string? CaseNumber, string? NatureOfCase, Party Court, LegalStatus? Status) : SofaPayload;

/// <summary>Line 10: property repossessed, foreclosed, garnished or attached.</summary>
public sealed record Repossession(Party Creditor, RepossessionAction? Action, string? Description, DateOnly? Date, decimal? Value) : SofaPayload;

/// <summary>Line 11: a setoff.</summary>
public sealed record Setoff(Party Creditor, string? Description, DateOnly? Date, decimal? Amount) : SofaPayload;

/// <summary>Line 12: property held by a receiver or custodian.</summary>
public sealed record Receivership(Party Custodian, string? Description, decimal? Value, string? CaseTitle, string? CaseNumber, Party Court, DateOnly? Date) : SofaPayload;

/// <summary>Line 13: gifts over the threshold.</summary>
public sealed record Gift(Party Recipient, string? Relationship, string? Description, ValueCollection<DateOnly> Dates, decimal? Value) : SofaPayload;

/// <summary>Line 14: charitable contributions over the threshold.</summary>
public sealed record CharitableContribution(Party Organization, string? Description, ValueCollection<DateOnly> Dates, decimal? Value) : SofaPayload;

/// <summary>Line 15: a loss from fire, theft, disaster or gambling.</summary>
public sealed record Loss(string? Description, string? InsuranceCoverage, DateOnly? Date, decimal? Value) : SofaPayload;

/// <summary>Line 16: a payment for bankruptcy advice or preparation.</summary>
public sealed record ConsultantPayment(Party Person, string? EmailOrWebsite, string? WhoMadePayment, string? Description, DateOnly? Date, decimal? Amount) : SofaPayload;

/// <summary>Line 17: a payment to someone who promised to help with creditors.</summary>
public sealed record CreditorAssistancePayment(Party Person, string? Description, DateOnly? Date, decimal? Amount) : SofaPayload;

/// <summary>Line 18: a transfer of property outside the ordinary course.</summary>
public sealed record PropertyTransfer(Party Transferee, string? Relationship, string? Description, string? ValueReceived, DateOnly? Date) : SofaPayload;

/// <summary>Line 19: a self-settled trust.</summary>
public sealed record SelfSettledTrust(string? TrustName, string? Description, DateOnly? Date) : SofaPayload;

/// <summary>Line 20: a closed financial account.</summary>
public sealed record ClosedAccount(
    Party Institution,
    [property: JsonPropertyName("account_last4")] string? AccountLast4,
    AccountType? AccountType,
    DateOnly? DateClosed,
    decimal? LastBalance) : SofaPayload;

/// <summary>Line 21: a safe deposit box.</summary>
public sealed record SafeDepositBox(Party Institution, ValueCollection<string> WhoHasAccess, string? Description, bool? StillHave) : SofaPayload;

/// <summary>Line 22: a storage unit.</summary>
public sealed record StorageUnit(Party Facility, ValueCollection<string> WhoHasAccess, string? Description, bool? StillHave) : SofaPayload;

/// <summary>Line 23: property held for someone else.</summary>
public sealed record HeldForAnother(Party Owner, string? Location, string? Description, decimal? Value) : SofaPayload;

/// <summary>Lines 24-25: an environmental notice received or release reported.</summary>
public sealed record EnvironmentalNotice(EnvironmentalNoticeKind? Kind, Party Site, Party GovernmentalUnit, string? EnvironmentalLaw, DateOnly? Date) : SofaPayload;

/// <summary>Line 26: an environmental proceeding.</summary>
public sealed record EnvironmentalProceeding(string? CaseTitle, string? CaseNumber, Party Court, string? NatureOfCase, LegalStatus? Status) : SofaPayload;

/// <summary>Line 27: a business connection.</summary>
public sealed record BusinessConnection(Party Business, string? NatureOfBusiness, string? Ein, DateOnly? FromDate, DateOnly? ToDate, ValueCollection<BusinessConnectionKind> Connection) : SofaPayload;

/// <summary>Line 28: a financial statement issued.</summary>
public sealed record FinancialStatementIssued(Party Recipient, DateOnly? DateIssued) : SofaPayload;

/// <summary>Line 1: marital status.</summary>
public sealed record MaritalStatus(MaritalStatusKind? Status) : SofaPayload;

/// <summary>Line 3: a community-property state lived in.</summary>
public sealed record CommunityPropertyResidence(string? State) : SofaPayload;

/// <summary>The consumer-debt declaration.</summary>
public sealed record ConsumerDebtDeclaration(bool? PrimarilyConsumerDebts) : SofaPayload;

/// <summary>One Form 107 entry: which question, and its answer.</summary>
public sealed record SofaEntryBody(SofaEntryType? EntryType, SofaPayload? Payload);

/// <summary>The SOFA collection.</summary>
public static class SofaRules
{
    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<SofaEntryBody> Kind = new("sofa_entry", "sofa_entries", "SOFA_ENTRY", Parse);

    private static readonly Dictionary<SofaEntryType, Func<JsonObject, Dictionary<string, string>, SofaPayload>> Parsers = new()
    {
        [SofaEntryType.MaritalStatus] = (raw, e) => new MaritalStatus(FieldParsers.Choice<MaritalStatusKind>(raw["status"], "payload.status", e)),
        [SofaEntryType.PriorAddress] = (raw, e) => new PriorAddress(
            FieldParsers.Choice<SofaDebtor>(raw["which_debtor"], "payload.which_debtor", e),
            FieldParsers.ParseAddress(raw["address"], "payload.address", e),
            FieldParsers.FormDate(raw["from_date"], "payload.from_date", e),
            FieldParsers.FormDate(raw["to_date"], "payload.to_date", e)),
        [SofaEntryType.CommunityPropertyResidence] = (raw, e) => new CommunityPropertyResidence(FieldParsers.Text(raw["state"], "payload.state", e, limit: 40)),
        [SofaEntryType.IncomeByPeriod] = (raw, e) => new IncomeByPeriod(
            FieldParsers.Choice<SofaDebtor>(raw["which_debtor"], "payload.which_debtor", e),
            FieldParsers.Choice<IncomeKind>(raw["kind"], "payload.kind", e),
            FieldParsers.Text(raw["description"], "payload.description", e),
            FieldParsers.FormDate(raw["period_start"], "payload.period_start", e),
            FieldParsers.FormDate(raw["period_end"], "payload.period_end", e),
            FieldParsers.Money(raw["gross_amount"], "payload.gross_amount", e)),
        [SofaEntryType.ConsumerDebtDeclaration] = (raw, e) => new ConsumerDebtDeclaration(FieldParsers.Boolean(raw["primarily_consumer_debts"], "payload.primarily_consumer_debts", e)),
        [SofaEntryType.CreditorPayment] = (raw, e) => new CreditorPayment(
            ParseParty(raw["creditor"], "payload.creditor", e),
            DateList(raw["dates"], "payload.dates", e),
            FieldParsers.Money(raw["total_paid"], "payload.total_paid", e),
            FieldParsers.Money(raw["amount_still_owed"], "payload.amount_still_owed", e),
            FieldParsers.ChoiceList<PaymentPurpose>(raw["payment_for"], "payload.payment_for", e),
            FieldParsers.Text(raw["payment_for_other"], "payload.payment_for_other", e)),
        [SofaEntryType.InsiderPayment] = (raw, e) => new InsiderPayment(
            ParseParty(raw["insider"], "payload.insider", e),
            FieldParsers.Text(raw["relationship"], "payload.relationship", e),
            DateList(raw["dates"], "payload.dates", e),
            FieldParsers.Money(raw["total_paid"], "payload.total_paid", e),
            FieldParsers.Money(raw["amount_still_owed"], "payload.amount_still_owed", e),
            FieldParsers.Narrative(raw["reason"], "payload.reason", e)),
        [SofaEntryType.InsiderBenefitPayment] = (raw, e) => new InsiderBenefitPayment(
            ParseParty(raw["recipient"], "payload.recipient", e),
            FieldParsers.Text(raw["insider_name"], "payload.insider_name", e),
            DateList(raw["dates"], "payload.dates", e),
            FieldParsers.Money(raw["total_paid"], "payload.total_paid", e),
            FieldParsers.Narrative(raw["reason"], "payload.reason", e)),
        [SofaEntryType.Lawsuit] = (raw, e) => new Lawsuit(
            FieldParsers.Text(raw["case_title"], "payload.case_title", e),
            FieldParsers.Text(raw["case_number"], "payload.case_number", e, limit: 64),
            FieldParsers.Text(raw["nature_of_case"], "payload.nature_of_case", e),
            ParseParty(raw["court"], "payload.court", e),
            FieldParsers.Choice<LegalStatus>(raw["status"], "payload.status", e)),
        [SofaEntryType.Repossession] = (raw, e) => new Repossession(
            ParseParty(raw["creditor"], "payload.creditor", e),
            FieldParsers.Choice<RepossessionAction>(raw["action"], "payload.action", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.FormDate(raw["date"], "payload.date", e),
            FieldParsers.Money(raw["value"], "payload.value", e)),
        [SofaEntryType.Setoff] = (raw, e) => new Setoff(
            ParseParty(raw["creditor"], "payload.creditor", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.FormDate(raw["date"], "payload.date", e),
            FieldParsers.Money(raw["amount"], "payload.amount", e)),
        [SofaEntryType.Receivership] = (raw, e) => new Receivership(
            ParseParty(raw["custodian"], "payload.custodian", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.Money(raw["value"], "payload.value", e),
            FieldParsers.Text(raw["case_title"], "payload.case_title", e),
            FieldParsers.Text(raw["case_number"], "payload.case_number", e, limit: 64),
            ParseParty(raw["court"], "payload.court", e),
            FieldParsers.FormDate(raw["date"], "payload.date", e)),
        [SofaEntryType.Gift] = (raw, e) => new Gift(
            ParseParty(raw["recipient"], "payload.recipient", e),
            FieldParsers.Text(raw["relationship"], "payload.relationship", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            DateList(raw["dates"], "payload.dates", e),
            FieldParsers.Money(raw["value"], "payload.value", e)),
        [SofaEntryType.CharitableContribution] = (raw, e) => new CharitableContribution(
            ParseParty(raw["organization"], "payload.organization", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            DateList(raw["dates"], "payload.dates", e),
            FieldParsers.Money(raw["value"], "payload.value", e)),
        [SofaEntryType.Loss] = (raw, e) => new Loss(
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.Narrative(raw["insurance_coverage"], "payload.insurance_coverage", e),
            FieldParsers.FormDate(raw["date"], "payload.date", e),
            FieldParsers.Money(raw["value"], "payload.value", e)),
        [SofaEntryType.ConsultantPayment] = (raw, e) => new ConsultantPayment(
            ParseParty(raw["person"], "payload.person", e),
            FieldParsers.Text(raw["email_or_website"], "payload.email_or_website", e),
            FieldParsers.Text(raw["who_made_payment"], "payload.who_made_payment", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.FormDate(raw["date"], "payload.date", e),
            FieldParsers.Money(raw["amount"], "payload.amount", e)),
        [SofaEntryType.CreditorAssistancePayment] = (raw, e) => new CreditorAssistancePayment(
            ParseParty(raw["person"], "payload.person", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.FormDate(raw["date"], "payload.date", e),
            FieldParsers.Money(raw["amount"], "payload.amount", e)),
        [SofaEntryType.PropertyTransfer] = (raw, e) => new PropertyTransfer(
            ParseParty(raw["transferee"], "payload.transferee", e),
            FieldParsers.Text(raw["relationship"], "payload.relationship", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.Narrative(raw["value_received"], "payload.value_received", e),
            FieldParsers.FormDate(raw["date"], "payload.date", e)),
        [SofaEntryType.SelfSettledTrust] = (raw, e) => new SelfSettledTrust(
            FieldParsers.Text(raw["trust_name"], "payload.trust_name", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.FormDate(raw["date"], "payload.date", e)),
        [SofaEntryType.ClosedAccount] = ParseClosedAccount,
        [SofaEntryType.SafeDepositBox] = (raw, e) => new SafeDepositBox(
            ParseParty(raw["institution"], "payload.institution", e),
            FieldParsers.StringList(raw["who_has_access"], "payload.who_has_access", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.Boolean(raw["still_have"], "payload.still_have", e)),
        [SofaEntryType.StorageUnit] = (raw, e) => new StorageUnit(
            ParseParty(raw["facility"], "payload.facility", e),
            FieldParsers.StringList(raw["who_has_access"], "payload.who_has_access", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.Boolean(raw["still_have"], "payload.still_have", e)),
        [SofaEntryType.HeldForAnother] = (raw, e) => new HeldForAnother(
            ParseParty(raw["owner"], "payload.owner", e),
            FieldParsers.Narrative(raw["location"], "payload.location", e),
            FieldParsers.Narrative(raw["description"], "payload.description", e),
            FieldParsers.Money(raw["value"], "payload.value", e)),
        [SofaEntryType.EnvironmentalNotice] = (raw, e) => new EnvironmentalNotice(
            FieldParsers.Choice<EnvironmentalNoticeKind>(raw["kind"], "payload.kind", e),
            ParseParty(raw["site"], "payload.site", e),
            ParseParty(raw["governmental_unit"], "payload.governmental_unit", e),
            FieldParsers.Text(raw["environmental_law"], "payload.environmental_law", e),
            FieldParsers.FormDate(raw["date"], "payload.date", e)),
        [SofaEntryType.EnvironmentalProceeding] = (raw, e) => new EnvironmentalProceeding(
            FieldParsers.Text(raw["case_title"], "payload.case_title", e),
            FieldParsers.Text(raw["case_number"], "payload.case_number", e, limit: 64),
            ParseParty(raw["court"], "payload.court", e),
            FieldParsers.Text(raw["nature_of_case"], "payload.nature_of_case", e),
            FieldParsers.Choice<LegalStatus>(raw["status"], "payload.status", e)),
        [SofaEntryType.BusinessConnection] = (raw, e) => new BusinessConnection(
            ParseParty(raw["business"], "payload.business", e),
            FieldParsers.Text(raw["nature_of_business"], "payload.nature_of_business", e),
            FieldParsers.Text(raw["ein"], "payload.ein", e, limit: 20),
            FieldParsers.FormDate(raw["from_date"], "payload.from_date", e),
            FieldParsers.FormDate(raw["to_date"], "payload.to_date", e),
            FieldParsers.ChoiceList<BusinessConnectionKind>(raw["connection"], "payload.connection", e)),
        [SofaEntryType.FinancialStatementIssued] = (raw, e) => new FinancialStatementIssued(
            ParseParty(raw["recipient"], "payload.recipient", e),
            FieldParsers.FormDate(raw["date_issued"], "payload.date_issued", e)),
    };

    /// <summary>
    /// Validates a SOFA entry (Python's <c>parse_sofa_entry</c>): the entry
    /// type names the payload's parser, so a payload without one is refused.
    /// </summary>
    public static SofaEntryBody Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var entryType = FieldParsers.Choice<SofaEntryType>(payload["entry_type"], "entry_type", errors);
        SofaPayload? parsed = null;
        var rawPayload = payload["payload"];
        if (rawPayload is not null)
        {
            if (entryType is null)
            {
                errors.TryAdd("entry_type", "Required when a payload is sent — it names the parser.");
            }
            else
            {
                parsed = Parsers[entryType.Value](FieldParsers.Mapping(rawPayload, "payload", errors), errors);
            }
        }

        IdRows.ThrowIfAny(errors);
        return new SofaEntryBody(entryType, parsed);
    }

    private static ClosedAccount ParseClosedAccount(JsonObject raw, Dictionary<string, string> e)
    {
        var accountLast4 = FieldParsers.Text(raw["account_last4"], "payload.account_last4", e, limit: 4);
        if (accountLast4 is not null && !PyText.IsDigits(accountLast4))
        {
            e["payload.account_last4"] = "Must be up to four digits.";
            accountLast4 = null;
        }

        return new ClosedAccount(
            ParseParty(raw["institution"], "payload.institution", e),
            accountLast4,
            FieldParsers.Choice<AccountType>(raw["account_type"], "payload.account_type", e),
            FieldParsers.FormDate(raw["date_closed"], "payload.date_closed", e),
            FieldParsers.Money(raw["last_balance"], "payload.last_balance", e));
    }

    private static Party ParseParty(JsonNode? value, string path, Dictionary<string, string> errors)
    {
        var raw = FieldParsers.Mapping(value, path, errors);
        return new Party(
            FieldParsers.Text(raw["name"], $"{path}.name", errors),
            FieldParsers.ParseAddress(raw["address"], $"{path}.address", errors));
    }

    private static ValueCollection<DateOnly> DateList(JsonNode? value, string path, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return [];
        }

        if (value is not JsonArray list)
        {
            errors[path] = "Must be a list of dates.";
            return [];
        }

        var dates = new List<DateOnly>();
        for (var index = 0; index < list.Count; index++)
        {
            if (FieldParsers.FormDate(list[index], $"{path}[{index}]", errors) is { } parsed)
            {
                dates.Add(parsed);
            }
        }

        return dates.ToValueCollection();
    }
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

/// <summary>Which Form 107 question an entry answers — the payload parser's key, in Python's <c>PAYLOAD_PARSERS</c> order.</summary>
public enum SofaEntryType
{
    /// <summary>The wire value <c>marital_status</c>.</summary>
    [JsonStringEnumMemberName("marital_status")]
    MaritalStatus,

    /// <summary>The wire value <c>prior_address</c>.</summary>
    [JsonStringEnumMemberName("prior_address")]
    PriorAddress,

    /// <summary>The wire value <c>community_property_residence</c>.</summary>
    [JsonStringEnumMemberName("community_property_residence")]
    CommunityPropertyResidence,

    /// <summary>The wire value <c>income_by_period</c>.</summary>
    [JsonStringEnumMemberName("income_by_period")]
    IncomeByPeriod,

    /// <summary>The wire value <c>consumer_debt_declaration</c>.</summary>
    [JsonStringEnumMemberName("consumer_debt_declaration")]
    ConsumerDebtDeclaration,

    /// <summary>The wire value <c>creditor_payment</c>.</summary>
    [JsonStringEnumMemberName("creditor_payment")]
    CreditorPayment,

    /// <summary>The wire value <c>insider_payment</c>.</summary>
    [JsonStringEnumMemberName("insider_payment")]
    InsiderPayment,

    /// <summary>The wire value <c>insider_benefit_payment</c>.</summary>
    [JsonStringEnumMemberName("insider_benefit_payment")]
    InsiderBenefitPayment,

    /// <summary>The wire value <c>lawsuit</c>.</summary>
    [JsonStringEnumMemberName("lawsuit")]
    Lawsuit,

    /// <summary>The wire value <c>repossession</c>.</summary>
    [JsonStringEnumMemberName("repossession")]
    Repossession,

    /// <summary>The wire value <c>setoff</c>.</summary>
    [JsonStringEnumMemberName("setoff")]
    Setoff,

    /// <summary>The wire value <c>receivership</c>.</summary>
    [JsonStringEnumMemberName("receivership")]
    Receivership,

    /// <summary>The wire value <c>gift</c>.</summary>
    [JsonStringEnumMemberName("gift")]
    Gift,

    /// <summary>The wire value <c>charitable_contribution</c>.</summary>
    [JsonStringEnumMemberName("charitable_contribution")]
    CharitableContribution,

    /// <summary>The wire value <c>loss</c>.</summary>
    [JsonStringEnumMemberName("loss")]
    Loss,

    /// <summary>The wire value <c>consultant_payment</c>.</summary>
    [JsonStringEnumMemberName("consultant_payment")]
    ConsultantPayment,

    /// <summary>The wire value <c>creditor_assistance_payment</c>.</summary>
    [JsonStringEnumMemberName("creditor_assistance_payment")]
    CreditorAssistancePayment,

    /// <summary>The wire value <c>property_transfer</c>.</summary>
    [JsonStringEnumMemberName("property_transfer")]
    PropertyTransfer,

    /// <summary>The wire value <c>self_settled_trust</c>.</summary>
    [JsonStringEnumMemberName("self_settled_trust")]
    SelfSettledTrust,

    /// <summary>The wire value <c>closed_account</c>.</summary>
    [JsonStringEnumMemberName("closed_account")]
    ClosedAccount,

    /// <summary>The wire value <c>safe_deposit_box</c>.</summary>
    [JsonStringEnumMemberName("safe_deposit_box")]
    SafeDepositBox,

    /// <summary>The wire value <c>storage_unit</c>.</summary>
    [JsonStringEnumMemberName("storage_unit")]
    StorageUnit,

    /// <summary>The wire value <c>held_for_another</c>.</summary>
    [JsonStringEnumMemberName("held_for_another")]
    HeldForAnother,

    /// <summary>The wire value <c>environmental_notice</c>.</summary>
    [JsonStringEnumMemberName("environmental_notice")]
    EnvironmentalNotice,

    /// <summary>The wire value <c>environmental_proceeding</c>.</summary>
    [JsonStringEnumMemberName("environmental_proceeding")]
    EnvironmentalProceeding,

    /// <summary>The wire value <c>business_connection</c>.</summary>
    [JsonStringEnumMemberName("business_connection")]
    BusinessConnection,

    /// <summary>The wire value <c>financial_statement_issued</c>.</summary>
    [JsonStringEnumMemberName("financial_statement_issued")]
    FinancialStatementIssued,
}
