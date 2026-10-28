using System.Text.Json.Nodes;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Sofa;

namespace Insolvia.Core.Tests;

/// <summary>The Statement of Financial Affairs, translated from test_sofa.py.</summary>
public sealed class SofaTests
{
    private static readonly Dictionary<string, string> SamplePayloads = new()
    {
        ["marital_status"] = """{"status": "married"}""",
        ["prior_address"] = """{"which_debtor": "both", "address": {"line1": "2 Former Rd", "city": "Oldtown"}, "from_date": "2023-01-01", "to_date": "2024-06-30"}""",
        ["community_property_residence"] = """{"state": "TX"}""",
        ["income_by_period"] = """{"which_debtor": "debtor_1", "kind": "wages_and_commissions", "description": "Example Employer", "period_start": "2025-01-01", "period_end": "2025-12-31", "gross_amount": "62000.00"}""",
        ["consumer_debt_declaration"] = """{"primarily_consumer_debts": true}""",
        ["creditor_payment"] = """{"creditor": {"name": "Example Bank", "address": {"city": "Exampleville"}}, "dates": ["2026-07-01", "2026-08-01"], "total_paid": "1300.00", "amount_still_owed": "8200.00", "payment_for": ["car"]}""",
        ["insider_payment"] = """{"insider": {"name": "Example Relative"}, "relationship": "brother", "dates": ["2026-03-15"], "total_paid": "900.00", "amount_still_owed": "0.00", "reason": "loan repayment"}""",
        ["insider_benefit_payment"] = """{"recipient": {"name": "Example Lender"}, "insider_name": "Example Relative", "dates": ["2026-02-01"], "total_paid": "450.00", "reason": "payment on a cosigned note"}""",
        ["lawsuit"] = """{"case_title": "Example Bank v. Debtor", "case_number": "26-cv-0001", "nature_of_case": "collection", "court": {"name": "Example County Court"}, "status": "pending"}""",
        ["repossession"] = """{"creditor": {"name": "Example Finance"}, "action": "repossessed", "description": "2014 pickup", "date": "2026-01-20", "value": "6000.00"}""",
        ["setoff"] = """{"creditor": {"name": "Example Credit Union"}, "description": "savings applied to loan", "date": "2026-07-15", "amount": "350.00"}""",
        ["receivership"] = """{"custodian": {"name": "Example Receiver LLC"}, "description": "rental property", "value": "150000.00", "case_title": "In re Example", "case_number": "26-mc-0002", "court": {"name": "Example District Court"}, "date": "2026-04-01"}""",
        ["gift"] = """{"recipient": {"name": "Example Recipient"}, "relationship": "niece", "description": "used car", "dates": ["2025-12-25"], "value": "700.00"}""",
        ["charitable_contribution"] = """{"organization": {"name": "Example Charity"}, "description": "cash", "dates": ["2025-11-30"], "value": "650.00"}""",
        ["loss"] = """{"description": "kitchen fire", "insurance_coverage": "homeowner's policy, claim pending", "date": "2026-02-10", "value": "4000.00"}""",
        ["consultant_payment"] = """{"person": {"name": "Example Law Firm"}, "email_or_website": "example.test", "who_made_payment": "debtor's mother", "description": "chapter 7 preparation", "date": "2026-08-01", "amount": "1500.00"}""",
        ["creditor_assistance_payment"] = """{"person": {"name": "Example Debt Relief Co"}, "description": "debt settlement plan", "date": "2026-05-01", "amount": "300.00"}""",
        ["property_transfer"] = """{"transferee": {"name": "Example Buyer"}, "relationship": "none", "description": "boat", "value_received": "cash at fair value", "date": "2025-09-01"}""",
        ["self_settled_trust"] = """{"trust_name": "Example Family Trust", "description": "brokerage account", "date": "2020-06-01"}""",
        ["closed_account"] = """{"institution": {"name": "Example Savings"}, "account_last4": "1234", "account_type": "savings", "date_closed": "2026-03-01", "last_balance": "80.00"}""",
        ["safe_deposit_box"] = """{"institution": {"name": "Example Bank"}, "who_has_access": ["Debtor", "Spouse"], "description": "documents and jewelry", "still_have": true}""",
        ["storage_unit"] = """{"facility": {"name": "Example Storage"}, "who_has_access": ["Debtor"], "description": "furniture", "still_have": false}""",
        ["held_for_another"] = """{"owner": {"name": "Example Neighbour"}, "location": "debtor's garage", "description": "motorcycle", "value": "3500.00"}""",
        ["environmental_notice"] = """{"kind": "liability_notice_received", "site": {"name": "Example Site", "address": {"city": "Exampleville"}}, "governmental_unit": {"name": "State EPA"}, "environmental_law": "state cleanup act", "date": "2025-10-01"}""",
        ["environmental_proceeding"] = """{"case_title": "State v. Example", "case_number": "25-env-0003", "court": {"name": "Example Admin Board"}, "nature_of_case": "cleanup order", "status": "concluded"}""",
        ["business_connection"] = """{"business": {"name": "Example Consulting LLC"}, "nature_of_business": "consulting", "ein": "12-3456789", "from_date": "2022-01-01", "to_date": "2025-12-31", "connection": ["sole_proprietor"]}""",
        ["financial_statement_issued"] = """{"recipient": {"name": "Example Bank"}, "date_issued": "2025-05-01"}""",
    };

    public static TheoryData<string> EntryTypes() => [.. SamplePayloads.Keys.Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_entry_type_has_a_sample() =>
        Assert.Equal(Wire.Names<SofaEntryType>().Order(StringComparer.Ordinal), SamplePayloads.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(EntryTypes))]
    public void A_valid_payload_parses_and_round_trips(string entryType)
    {
        var sent = Body.Of(SamplePayloads[entryType]);
        var body = SofaRules.Parse(new JsonObject { ["entry_type"] = entryType, ["payload"] = sent.DeepClone() });
        Assert.Equal(entryType, body.EntryType!.Value.ToWire());
        Assert.NotNull(body.Payload);
        var stored = BodyJson.Prune(BodyJson.ToNode(body.Payload));
        Assert.True(JsonNode.DeepEquals(sent, stored), $"{sent.ToJsonString()} != {stored.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(EntryTypes))]
    public void An_empty_payload_is_valid_for_every_type(string entryType) =>
        Assert.NotNull(SofaRules.Parse(new JsonObject { ["entry_type"] = entryType, ["payload"] = new JsonObject() }).Payload);

    [Fact]
    public void A_payload_without_a_type_is_refused() =>
        Assert.Contains("entry_type", Assert.Throws<FieldValidationException>(() => SofaRules.Parse(Body.Of("""{"payload": {"status": "married"}}"""))).Fields.Keys);

    [Fact]
    public void An_unknown_entry_type_is_refused() =>
        Assert.Contains("entry_type", Assert.Throws<FieldValidationException>(() => SofaRules.Parse(Body.Of("""{"entry_type": "question_29", "payload": {}}"""))).Fields.Keys);

    [Fact]
    public void A_type_with_no_payload_yet_is_valid()
    {
        var body = SofaRules.Parse(Body.Of("""{"entry_type": "lawsuit"}"""));
        Assert.Equal(SofaEntryType.Lawsuit, body.EntryType);
        Assert.Null(body.Payload);
    }

    [Theory]
    [InlineData("marital_status", """{"status": "divorced"}""", "payload.status")]
    [InlineData("gift", """{"value": "sev hundred"}""", "payload.value")]
    [InlineData("prior_address", """{"from_date": "2023-13-01"}""", "payload.from_date")]
    [InlineData("closed_account", """{"account_last4": "12ab"}""", "payload.account_last4")]
    [InlineData("creditor_payment", """{"dates": ["yesterday"]}""", "payload.dates[0]")]
    [InlineData("consumer_debt_declaration", """{"primarily_consumer_debts": "yes"}""", "payload.primarily_consumer_debts")]
    [InlineData("business_connection", """{"connection": ["landlord"]}""", "payload.connection[0]")]
    public void A_malformed_payload_field_is_named_in_the_error(string entryType, string payload, string badPath) =>
        Assert.Contains(badPath, Assert.Throws<FieldValidationException>(() => SofaRules.Parse(new JsonObject { ["entry_type"] = entryType, ["payload"] = Body.Of(payload) })).Fields.Keys);
}
