using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Sofa;

namespace Insolvia.Domain.Tests;

/// <summary>The Statement of Financial Affairs, translated from test_sofa.py.</summary>
public sealed class SofaTests
{
    private static readonly Dictionary<string, string> SamplePayloads = new()
    {
        ["marital_status"] = """{"status": "married"}""",
        ["prior_address"] = """{"whichDebtor": "both", "address": {"line1": "2 Former Rd", "city": "Oldtown"}, "fromDate": "2023-01-01", "toDate": "2024-06-30"}""",
        ["community_property_residence"] = """{"state": "TX"}""",
        ["income_by_period"] = """{"whichDebtor": "debtor_1", "kind": "wages_and_commissions", "description": "Example Employer", "periodStart": "2025-01-01", "periodEnd": "2025-12-31", "grossAmount": 62000.00}""",
        ["consumer_debt_declaration"] = """{"primarilyConsumerDebts": true}""",
        ["creditor_payment"] = """{"creditor": {"name": "Example Bank", "address": {"city": "Exampleville"}}, "dates": ["2026-07-01", "2026-08-01"], "totalPaid": 1300.00, "amountStillOwed": 8200.00, "paymentFor": ["car"]}""",
        ["insider_payment"] = """{"insider": {"name": "Example Relative"}, "relationship": "brother", "dates": ["2026-03-15"], "totalPaid": 900.00, "amountStillOwed": 0.00, "reason": "loan repayment"}""",
        ["insider_benefit_payment"] = """{"recipient": {"name": "Example Lender"}, "insiderName": "Example Relative", "dates": ["2026-02-01"], "totalPaid": 450.00, "reason": "payment on a cosigned note"}""",
        ["lawsuit"] = """{"caseTitle": "Example Bank v. Debtor", "caseNumber": "26-cv-0001", "natureOfCase": "collection", "court": {"name": "Example County Court"}, "status": "pending"}""",
        ["repossession"] = """{"creditor": {"name": "Example Finance"}, "action": "repossessed", "description": "2014 pickup", "date": "2026-01-20", "value": 6000.00}""",
        ["setoff"] = """{"creditor": {"name": "Example Credit Union"}, "description": "savings applied to loan", "date": "2026-07-15", "amount": 350.00}""",
        ["receivership"] = """{"custodian": {"name": "Example Receiver LLC"}, "description": "rental property", "value": 150000.00, "caseTitle": "In re Example", "caseNumber": "26-mc-0002", "court": {"name": "Example District Court"}, "date": "2026-04-01"}""",
        ["gift"] = """{"recipient": {"name": "Example Recipient"}, "relationship": "niece", "description": "used car", "dates": ["2025-12-25"], "value": 700.00}""",
        ["charitable_contribution"] = """{"organization": {"name": "Example Charity"}, "description": "cash", "dates": ["2025-11-30"], "value": 650.00}""",
        ["loss"] = """{"description": "kitchen fire", "insuranceCoverage": "homeowner's policy, claim pending", "date": "2026-02-10", "value": 4000.00}""",
        ["consultant_payment"] = """{"person": {"name": "Example Law Firm"}, "emailOrWebsite": "example.test", "whoMadePayment": "debtor's mother", "description": "chapter 7 preparation", "date": "2026-08-01", "amount": 1500.00}""",
        ["creditor_assistance_payment"] = """{"person": {"name": "Example Debt Relief Co"}, "description": "debt settlement plan", "date": "2026-05-01", "amount": 300.00}""",
        ["property_transfer"] = """{"transferee": {"name": "Example Buyer"}, "relationship": "none", "description": "boat", "valueReceived": "cash at fair value", "date": "2025-09-01"}""",
        ["self_settled_trust"] = """{"trustName": "Example Family Trust", "description": "brokerage account", "date": "2020-06-01"}""",
        ["closed_account"] = """{"institution": {"name": "Example Savings"}, "accountLast4": "1234", "accountType": "savings", "dateClosed": "2026-03-01", "lastBalance": 80.00}""",
        ["safe_deposit_box"] = """{"institution": {"name": "Example Bank"}, "whoHasAccess": ["Debtor", "Spouse"], "description": "documents and jewelry", "stillHave": true}""",
        ["storage_unit"] = """{"facility": {"name": "Example Storage"}, "whoHasAccess": ["Debtor"], "description": "furniture", "stillHave": false}""",
        ["held_for_another"] = """{"owner": {"name": "Example Neighbour"}, "location": "debtor's garage", "description": "motorcycle", "value": 3500.00}""",
        ["environmental_notice"] = """{"kind": "liability_notice_received", "site": {"name": "Example Site", "address": {"city": "Exampleville"}}, "governmentalUnit": {"name": "State EPA"}, "environmentalLaw": "state cleanup act", "date": "2025-10-01"}""",
        ["environmental_proceeding"] = """{"caseTitle": "State v. Example", "caseNumber": "25-env-0003", "court": {"name": "Example Admin Board"}, "natureOfCase": "cleanup order", "status": "concluded"}""",
        ["business_connection"] = """{"business": {"name": "Example Consulting LLC"}, "natureOfBusiness": "consulting", "ein": "12-3456789", "fromDate": "2022-01-01", "toDate": "2025-12-31", "connection": ["sole_proprietor"]}""",
        ["financial_statement_issued"] = """{"recipient": {"name": "Example Bank"}, "dateIssued": "2025-05-01"}""",
    };

    public static TheoryData<string> EntryTypes() => [.. SamplePayloads.Keys.Order(StringComparer.Ordinal)];

    private static SofaEntryBody Read(string entryType, JsonObject payload)
    {
        payload["entryType"] = entryType;
        return (SofaEntryBody)BodyJson.Read(new JsonObject { ["payload"] = payload }, typeof(SofaEntryBody));
    }

    [Fact]
    public void Every_entry_type_has_a_sample() =>
        Assert.Equal(
            typeof(SofaPayload).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(derived => (string)derived.TypeDiscriminator!).Order(StringComparer.Ordinal),
            SamplePayloads.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(EntryTypes))]
    public void A_valid_payload_reads_and_round_trips(string entryType)
    {
        var sent = Body.Of(SamplePayloads[entryType]);
        var body = Read(entryType, (JsonObject)sent.DeepClone());
        sent["entryType"] = entryType;
        var stored = BodyJson.ToNode(body)["payload"]!;
        Assert.True(JsonNode.DeepEquals(sent, stored), $"{sent.ToJsonString()} != {stored.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(EntryTypes))]
    public void An_empty_payload_is_valid_for_every_type(string entryType) =>
        Assert.NotEqual(typeof(SofaPayload), Read(entryType, []).Payload!.GetType());

    [Fact]
    public void A_payload_without_a_type_is_refused() =>
        Assert.Contains("payload.entryType", Assert.Throws<FieldValidationException>(() => BodyJson.Read(Body.Of("""{"payload": {"status": "married"}}"""), typeof(SofaEntryBody))).Fields.Keys);

    [Fact]
    public void An_unknown_entry_type_is_refused() =>
        Assert.Throws<FieldValidationException>(() => Read("question_29", []));

    [Fact]
    public void An_entry_with_no_payload_yet_is_valid() =>
        Assert.Null(((SofaEntryBody)BodyJson.Read(new JsonObject(), typeof(SofaEntryBody))).Payload);

    [Theory]
    [InlineData("marital_status", """{"status": "divorced"}""", "payload.status")]
    [InlineData("gift", """{"value": -1}""", "payload.value")]
    [InlineData("prior_address", """{"fromDate": "2023-13-01"}""", "payload.fromDate")]
    public void A_malformed_payload_field_is_named_in_the_error(string entryType, string payload, string badPath) =>
        Assert.Contains(badPath, Assert.Throws<FieldValidationException>(() => Read(entryType, Body.Of(payload))).Fields.Keys);
}
