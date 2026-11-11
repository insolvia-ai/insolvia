using System.Reflection;
using System.Text.Json.Nodes;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Claims;
using Insolvia.Domain.Creditors;
using Insolvia.Domain.Fields;
using Insolvia.Domain.Income;
using Insolvia.Domain.MeansTest;
using Insolvia.Domain.Provenance;

namespace Insolvia.Domain.Tests;

/// <summary>The machinery every case collection shares, translated from test_case_entities.py.</summary>
public sealed class CaseEntityTests
{
    private const string CaseId = "case-0001";

    /// <summary>A populated body for every collection, every member set — Python's <c>SAMPLE_BODIES</c>.</summary>
    internal static readonly IReadOnlyDictionary<string, string> SampleBodies = new Dictionary<string, string>
    {
        ["creditors"] = """{"name": "Example Bank", "address": {"line1": "1 Example Way"}}""",
        ["claims"] = """
            {"creditorId": "cr-1", "claimClass": "secured", "amount": 12500.00, "contingent": false, "assetId": "as-1",
             "lienPosition": 1, "collateralDescription": "2016 sedan", "collateralValue": 9000.00, "lienNature": ["agreement"],
             "unsecuredAmountOverride": 3500.00, "intention": "retain_reaffirm", "intentionExplanation": "Keeping the car; payments are current.",
             "noticeParties": [{"id": "np1", "name": "Example Servicer"}]}
            """,
        ["assets"] = """
            {"category": "vehicle", "description": "2016 sedan, 90k miles", "valueEntire": 9000.00, "valuePortionOwned": 9000.00,
             "ownershipInterest": "debtor_1", "communityProperty": false, "year": 2016, "make": "Example Motors", "model": "Sedan LX",
             "mileage": 90000, "includesPersonalInformation": false}
            """,
        ["employments"] = """
            {"debtorId": "d-1", "status": "employed", "occupation": "Example occupation", "employerName": "Example Employer",
             "employerAddress": {"city": "Exampleville"}, "employedSince": "2019-02-14"}
            """,
        ["income_summaries"] = """{"debtorId": "d-1", "wages": 5200.00, "deductionTax": 830.00, "changeExpected": false}""",
        ["pay_period_records"] = """
            {"employmentId": "emp-1", "periodStart": "2026-08-01", "periodEnd": "2026-08-14", "payDate": "2026-08-19",
             "gross": 2400.00, "net": 1890.50, "frequency": "biweekly",
             "deductions": [{"id": "ded1", "category": "tax", "amount": 410.00},
                            {"id": "ded2", "category": "insurance", "amount": 99.50, "description": "Medical premium"}]}
            """,
        ["households"] = """{"whichHousehold": "main", "separateHousehold": false}""",
        ["expenses"] = """{"householdId": "h-1", "category": "rent_or_home_ownership", "amount": 1800.00}""",
        ["dependents"] = """{"householdId": "h-1", "relationship": "daughter", "age": 9, "livesWithDebtor": true}""",
        ["codebtors"] = """{"name": "Example Cosigner", "address": {"city": "Exampleville"}, "claimIds": ["cl-1"]}""",
        ["sofa_entries"] = """
            {"payload": {"entryType": "gift", "recipient": {"name": "Example Recipient"}, "relationship": "niece",
             "dates": ["2025-12-25"], "value": 700.00}}
            """,
        ["petitions"] = """
            {"feeHandling": "installments", "rentsResidence": true, "evictionJudgmentAgainstYou": false,
             "smallBusinessStatus": "not_filing_under_chapter_11",
             "hazardousProperty": {"description": "Propane tanks at a former stand", "whyImmediate": "Leak risk", "address": {"city": "Exampleville"}},
             "debtCharacter": "consumer", "ch7FundsAvailableForCreditors": false, "estimatedCreditors": "1_49",
             "estimatedAssets": "0_50000", "estimatedLiabilities": "50001_100000"}
            """,
        ["prior_cases"] = """{"district": "Middle District of Florida", "filedOn": "2019-03-04", "caseNumber": "19-01234"}""",
        ["related_cases"] = """
            {"debtorName": "Example Spouse", "relationship": "Spouse", "district": "Middle District of Florida",
             "filedOn": "2026-01-15", "caseNumber": "26-00042"}
            """,
        ["sole_proprietorships"] = """{"name": "Example Lawn Care", "address": {"city": "Exampleville"}, "businessType": "none_of_the_above"}""",
        ["filing_professionals"] = """
            {"role": "attorney", "name": {"given": "Alex", "surname": "Counsel"}, "firmName": "Counsel & Counsel PA",
             "address": {"line1": "1 Example Way", "city": "Exampleville"}, "phone": "(305) 555-0100", "email": "alex@example.com",
             "barNumber": "112233", "barState": "FL", "signatureDate": "2026-09-01"}
            """,
        ["exemptions"] = """
            {"assetId": "as-1", "statuteCitation": "Fla. Stat. § 222.25(1)", "amount": 5000.00, "claimsFullFmv": false,
             "acquiredWithin1215Days": false}
            """,
        ["contract_leases"] = """
            {"counterpartyName": "Example Storage LLC", "counterpartyAddress": {"line1": "1 Example Way", "city": "Exampleville"},
             "description": "Month-to-month storage unit lease, unit 12", "intention": "assume", "listOnStatementOfIntention": true}
            """,
        ["community_household_members"] = """
            {"name": "Example Former Spouse", "address": {"city": "Exampleville", "state": "TX"}, "communityState": "TX", "livedWithDebtor": false}
            """,
        ["other_income_records"] = """
            {"debtorId": "d-1", "category": "unemployment", "receivedOn": "2026-04-07", "amount": 275.00, "payer": "Example State Agency"}
            """,
        ["means_test_inputs"] = """
            {"peopleUnder65": 3, "people65OrOlder": 0,
             "maritalAdjustments": [{"id": "ma1", "description": "Spouse's own student loan", "amount": 220.00}],
             "homeSecuredMonthlyTotal": 1450.00, "vehicleCount": 1, "vehicle1LoanMonthly": 310.00, "taxes": 780.00,
             "involuntaryDeductions": 95.00, "healthInsurance": 410.00,
             "otherSecuredPayments": [{"id": "os1", "creditorName": "Example Finance", "propertyDescription": "Boat", "monthlyPayment": 120.00}],
             "priorityCureTotal": 2400.00, "ch13Eligible": true, "ch13ProjectedPlanPayment": 500.00,
             "maritalFilingStatus": "married_not_filing_same_household", "medianHouseholdSize": 3, "irsFamilySize": 2,
             "irsHousingFamilySize": 3, "nonConsumerDebts": false, "disabledVeteran": false, "reservistNationalGuard": false,
             "incomeOverrides": [{"id": "ov1", "column": "A", "category": "wages", "monthlyAmount": 4100.00}]}
            """,
        ["plans"] = """
            {"termMonths": 60, "paymentSource": "fixed", "monthlyPayment": 850.00,
             "stepPayments": [{"id": "st1", "startMonth": 25, "monthlyPayment": 1000.00}],
             "lumpSums": [{"id": "ls1", "month": 13, "amount": 2000.00, "description": "Refund"}],
             "trusteePercentage": 8.5, "attorneyFees": 3500.00,
             "securedTreatments": [{"id": "tr1", "claimId": "c-1", "treatment": "cramdown", "cramdownValue": 9000.00, "interestRate": 7.25}],
             "priorityPercentage": 100, "unsecuredTreatment": "pot", "chapter7OtherCosts": 1500.00}
            """,
    };

    public static TheoryData<string> Collections() => [.. CaseCollections.ByCollection.Keys.Order(StringComparer.Ordinal)];

    private static object Read(string collection) => BodyJson.Read(Body.Of(SampleBodies[collection]), CaseCollections.ByCollection[collection].BodyType);

    /// <summary>A staff_typed entry for every populated path — built with the server's own walk.</summary>
    internal static ValueDictionary<string, ProvenanceEntry> TypedProvenance(object body) =>
        ProvenanceRules.PopulatedPaths(BodyJson.ToNode(body)).ToDictionary(path => path, _ => new ProvenanceEntry(ProvenanceSource.StaffTyped)).ToValueDictionary();

    [Fact]
    public void Every_collection_has_a_sample_body() =>
        Assert.Equal(CaseCollections.ByCollection.Keys.Order(StringComparer.Ordinal), SampleBodies.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void Collection_keys_match_their_kinds() =>
        Assert.All(CaseCollections.ByCollection, pair => Assert.Equal(pair.Key, pair.Value.Collection));

    [Fact]
    public void Sort_key_namespaces_cannot_collide()
    {
        var all = CaseCollections.Kinds.Select(kind => kind.SkPrefix).Concat(CaseCollections.ReservedNamespaces).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public void A_populated_body_with_full_provenance_is_accepted(string collection)
    {
        var body = Read(collection);
        CaseEntityRules.RequireProvenance(body, TypedProvenance(body), stored: null);
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public void A_populated_field_without_provenance_is_rejected(string collection)
    {
        var error = Assert.Throws<FieldValidationException>(() => CaseEntityRules.RequireProvenance(Read(collection), ValueCollections.EmptyDictionary<string, ProvenanceEntry>(), stored: null));
        Assert.All(error.Fields.Keys, path => Assert.StartsWith("provenance.", path, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public void An_empty_body_needs_no_provenance(string collection) =>
        CaseEntityRules.RequireProvenance(BodyJson.Read(new JsonObject(), CaseCollections.ByCollection[collection].BodyType), ValueCollections.EmptyDictionary<string, ProvenanceEntry>(), stored: null);

    [Fact]
    public void An_unconfirmed_machine_value_cannot_be_stored()
    {
        var error = Assert.Throws<FieldValidationException>(() => ProvenanceRules.Check(new Dictionary<string, ProvenanceEntry> { ["name"] = new(ProvenanceSource.AiExtracted) }));
        Assert.Contains("provenance.name", error.Fields.Keys);
    }

    [Fact]
    public void A_confirmed_machine_value_is_accepted()
    {
        var entries = ProvenanceRules.Parse(Body.Of("""
            {"name": {"source": "ai_extracted", "confirmed_by": "00000000-0000-4000-8000-00000000a11c",
             "confirmed_at": "2026-08-01T12:00:00.000000Z", "confidence": 0.93}}
            """));
        Assert.NotNull(entries["name"].ConfirmedBy);
    }

    // The stored body is the record serialized (ADR 0032): read back, it is the same body.
    [Theory]
    [MemberData(nameof(Collections))]
    public void A_body_survives_the_stored_round_trip(string collection)
    {
        var body = Read(collection);
        Assert.Equal(body, BodyJson.Read(BodyJson.ToNode(body), body.GetType()));
    }

    // Unknown members are ignored, so a sample member the record does not
    // read would vanish silently — every one must come back out.
    [Fact]
    public void Every_sample_member_is_named_as_its_record_reads_it()
    {
        foreach (var collection in CaseCollections.ByCollection.Keys)
        {
            var sent = Body.Of(SampleBodies[collection]);
            var stored = BodyJson.ToNode(Read(collection));
            Assert.True(JsonNode.DeepEquals(sent, stored), $"{collection}: {sent.ToJsonString()} != {stored.ToJsonString()}");
        }
    }

    [Fact]
    public void The_stored_body_omits_absent_members_and_keeps_false()
    {
        var body = BodyJson.ToNode(new ClaimBody(CreditorId: "cr-1", Contingent: false));
        Assert.False((bool)body["contingent"]!);
        Assert.False(body.ContainsKey("disputed"));
        Assert.False(body.ContainsKey("noticeParties"));
    }

    [Fact]
    public void A_new_record_is_not_amended() =>
        Assert.False(CaseEntityRules.Create(CreditorRules.Kind, CaseId, new CreditorBody(), ValueCollections.EmptyDictionary<string, ProvenanceEntry>()).Amended);

    // ── Every field carries its rule (ADR 0032) ────────────────────

    // A string, number or count with no attribute would accept anything; a
    // new member must say which rule it follows. A row's id is checked by
    // [Rows] on its list.
    [Fact]
    public void Every_scalar_body_member_has_a_field_attribute()
    {
        var missing = new List<string>();
        var seen = new HashSet<Type>();
        foreach (var kind in CaseCollections.Kinds.Append(Notes.NoteRules.Kind))
        {
            Walk(kind.BodyType, kind.BodyType.Name);
        }

        Assert.Empty(missing);

        void Walk(Type type, string path)
        {
            if (!seen.Add(type))
            {
                return;
            }

            var derived = type.GetCustomAttributes<System.Text.Json.Serialization.JsonDerivedTypeAttribute>().Select(attribute => attribute.DerivedType);
            foreach (var each in derived)
            {
                Walk(each, $"{path}<{each.Name}>");
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var member = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                var item = member.IsGenericType && member.GetGenericArguments() is [var only] && member != typeof(string) ? only : member;
                if (item == typeof(string) || item == typeof(decimal) || item == typeof(int))
                {
                    var ruled = property.GetCustomAttributes<System.ComponentModel.DataAnnotations.ValidationAttribute>().Any();
                    if (!ruled && !(property.Name == nameof(IRow.Id) && typeof(IRow).IsAssignableFrom(type)))
                    {
                        missing.Add($"{path}.{property.Name}");
                    }
                }
                else if (item.Namespace?.StartsWith("Insolvia.Domain", StringComparison.Ordinal) == true && item.IsClass)
                {
                    Walk(item, $"{path}.{property.Name}");
                }
            }
        }
    }

    // ── The cross-field rules (IValidatableObject) ─────────────────

    [Fact]
    public void A_lien_position_of_zero_is_refused() =>
        Assert.Contains("lienPosition", Errors<ClaimBody>("""{"lienPosition": 0}"""));

    [Fact]
    public void Expenses_belong_only_to_business_and_rental_receipts() =>
        Assert.Contains("expenses", Errors<OtherIncomeRecordBody>("""{"category": "unemployment", "amount": 275.00, "expenses": 10.00}"""));

    [Fact]
    public void A_means_test_household_has_at_least_one_person() =>
        Assert.Contains("irsFamilySize", Errors<MeansTestInputBody>("""{"irsFamilySize": 0}"""));

    [Fact]
    public void More_than_two_vehicles_is_refused() =>
        Assert.Contains("vehicleCount", Errors<MeansTestInputBody>("""{"vehicleCount": 3}"""));

    [Fact]
    public void A_notice_party_without_an_id_is_refused() =>
        Assert.Contains("noticeParties[0].id", Errors<ClaimBody>("""{"noticeParties": [{"name": "Example Servicer"}]}"""));

    private static IEnumerable<string> Errors<T>(string json) =>
        Assert.Throws<FieldValidationException>(() => Body.Read<T>(Body.Of(json))).Fields.Keys;
}
