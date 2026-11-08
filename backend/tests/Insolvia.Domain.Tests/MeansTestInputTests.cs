using System.Globalization;
using System.Text.Json.Nodes;
using Insolvia.Core.MeansTest;

namespace Insolvia.Core.Tests;

/// <summary>B122A-2's entered figures, translated from test_means_test_inputs.py.</summary>
public sealed class MeansTestInputTests
{
    public static TheoryData<MaritalFilingStatus> MaritalStatuses() => [.. Wire.Members<MaritalFilingStatus>()];

    public static TheoryData<IncomeLineCategory> IncomeLines() => [.. Wire.Members<IncomeLineCategory>()];

    public static TheoryData<SecuredPaymentBucket> Buckets() => [.. Wire.Members<SecuredPaymentBucket>()];

    [Fact]
    public void An_empty_payload_is_an_empty_record()
    {
        var body = MeansTestInputRules.Parse([]);
        Assert.Null(body.MaritalFilingStatus);
        Assert.Null(body.MedianHouseholdSize);
        Assert.Null(body.IrsFamilySize);
        Assert.Null(body.IrsHousingFamilySize);
        Assert.Null(body.NonConsumerDebts);
        Assert.Null(body.DisabledVeteran);
        Assert.Null(body.ReservistNationalGuard);
        Assert.Empty(body.IncomeOverrides);
        Assert.Empty(body.OtherSecuredPayments);
    }

    [Theory]
    [MemberData(nameof(MaritalStatuses))]
    public void Every_marital_status_is_accepted(MaritalFilingStatus status) =>
        Assert.Equal(status, MeansTestInputRules.Parse(new JsonObject { ["marital_filing_status"] = status.ToWire() }).MaritalFilingStatus);

    [Fact]
    public void An_unknown_marital_status_is_rejected() =>
        Assert.Contains("marital_filing_status", ErrorsOf("""{"marital_filing_status": "widowed"}""").Keys);

    [Theory]
    [InlineData("median_household_size")]
    [InlineData("irs_family_size")]
    [InlineData("irs_housing_family_size")]
    public void A_household_size_override_must_have_at_least_one_person(string field)
    {
        _ = MeansTestInputRules.Parse(new JsonObject { [field] = 3 });
        Assert.Contains(field, ErrorsOf(new JsonObject { [field] = 0 }).Keys);
        Assert.Contains(field, ErrorsOf(new JsonObject { [field] = "3" }).Keys);
    }

    [Fact]
    public void An_exemption_is_a_yes_no_answer()
    {
        Assert.True(MeansTestInputRules.Parse(Body.Of("""{"non_consumer_debts": true}""")).NonConsumerDebts);
        Assert.False(MeansTestInputRules.Parse(Body.Of("""{"disabled_veteran": false}""")).DisabledVeteran);
        Assert.True(MeansTestInputRules.Parse(Body.Of("""{"reservist_national_guard": true}""")).ReservistNationalGuard);
        foreach (var flag in new[] { "non_consumer_debts", "disabled_veteran", "reservist_national_guard" })
        {
            Assert.Contains(flag, ErrorsOf(new JsonObject { [flag] = "yes" }).Keys);
        }
    }

    [Fact]
    public void An_income_override_row_parses_whole()
    {
        var row = Assert.Single(MeansTestInputRules.Parse(Body.Of("""{"income_overrides": [{"id": "ov-1", "column": "A", "category": "wages", "monthly_amount": "5000"}]}""")).IncomeOverrides);
        Assert.Equal(("ov-1", IncomeColumn.A, IncomeLineCategory.Wages, "5000.00"), (row.Id, row.Column!.Value, row.Category!.Value, Text(row.MonthlyAmount)));
    }

    [Theory]
    [MemberData(nameof(IncomeLines))]
    public void Every_income_line_including_the_exclusions_can_be_overridden(IncomeLineCategory category) =>
        Assert.Equal(category, MeansTestInputRules.Parse(Body.Of($$"""{"income_overrides": [{"id": "r", "column": "B", "category": "{{category.ToWire()}}"}]}""")).IncomeOverrides[0].Category);

    [Fact]
    public void An_income_override_needs_an_addressable_id() =>
        Assert.Contains("income_overrides[0].id", ErrorsOf("""{"income_overrides": [{"column": "A", "category": "wages"}]}""").Keys);

    [Fact]
    public void An_income_override_column_is_a_or_b() =>
        Assert.Contains("income_overrides[0].column", ErrorsOf("""{"income_overrides": [{"id": "r", "column": "C"}]}""").Keys);

    [Fact]
    public void An_income_override_category_must_be_a_form_line() =>
        Assert.Contains("income_overrides[0].category", ErrorsOf("""{"income_overrides": [{"id": "r", "category": "lottery"}]}""").Keys);

    [Fact]
    public void Two_overrides_for_one_columns_line_are_refused() =>
        Assert.Contains("income_overrides[1].category", ErrorsOf("""{"income_overrides": [{"id": "a", "column": "A", "category": "wages"}, {"id": "b", "column": "A", "category": "wages"}]}""").Keys);

    [Fact]
    public void The_same_line_may_be_overridden_in_each_column() =>
        Assert.Equal(2, MeansTestInputRules.Parse(Body.Of("""{"income_overrides": [{"id": "a", "column": "A", "category": "wages"}, {"id": "b", "column": "B", "category": "wages"}]}""")).IncomeOverrides.Count);

    [Fact]
    public void A_secured_payment_row_carries_its_claim_bucket_and_cure()
    {
        var row = Assert.Single(MeansTestInputRules.Parse(Body.Of("""
            {"other_secured_payments": [{"id": "sp-1", "claim_id": "claim-0001", "bucket": "vehicle_1", "monthly_payment": "415", "cure_total": "1200.50"}]}
            """)).OtherSecuredPayments);
        Assert.Equal(("claim-0001", SecuredPaymentBucket.Vehicle1, "415.00", "1200.50"), (row.ClaimId, row.Bucket!.Value, Text(row.MonthlyPayment), Text(row.CureTotal)));
    }

    [Theory]
    [MemberData(nameof(Buckets))]
    public void Every_ownership_bucket_is_accepted(SecuredPaymentBucket bucket) =>
        Assert.Equal(bucket, MeansTestInputRules.Parse(Body.Of($$"""{"other_secured_payments": [{"id": "r", "bucket": "{{bucket.ToWire()}}"}]}""")).OtherSecuredPayments[0].Bucket);

    [Fact]
    public void An_unknown_bucket_is_rejected() =>
        Assert.Contains("other_secured_payments[0].bucket", ErrorsOf("""{"other_secured_payments": [{"id": "r", "bucket": "boat"}]}""").Keys);

    [Fact]
    public void A_row_without_the_new_members_still_parses()
    {
        var row = Assert.Single(MeansTestInputRules.Parse(Body.Of("""
            {"other_secured_payments": [{"id": "os-1", "creditor_name": "Example Finance", "property_description": "Boat", "monthly_payment": "120.00"}]}
            """)).OtherSecuredPayments);
        Assert.Null(row.ClaimId);
        Assert.Null(row.Bucket);
        Assert.Null(row.CureTotal);
    }

    [Fact]
    public void The_chapter_13_answers_parse_on_the_same_record()
    {
        var body = MeansTestInputRules.Parse(Body.Of("""
            {"commitment_period_marital_adjustment": true, "child_support_for_dependents": "350", "qualified_retirement_deductions": "210.5",
             "special_circumstances": [{"id": "sc-1", "description": "Dialysis travel", "amount": "180"}]}
            """));
        Assert.True(body.CommitmentPeriodMaritalAdjustment);
        Assert.Equal("350.00", Text(body.ChildSupportForDependents));
        Assert.Equal("210.50", Text(body.QualifiedRetirementDeductions));
        var row = Assert.Single(body.SpecialCircumstances);
        Assert.Equal(("sc-1", "Dialysis travel", "180.00"), (row.Id, row.Description, Text(row.Amount)));
    }

    [Fact]
    public void The_chapter_13_answers_are_absent_by_default()
    {
        var body = MeansTestInputRules.Parse([]);
        Assert.Null(body.CommitmentPeriodMaritalAdjustment);
        Assert.Null(body.ChildSupportForDependents);
        Assert.Null(body.QualifiedRetirementDeductions);
        Assert.Empty(body.SpecialCircumstances);
    }

    [Fact]
    public void A_special_circumstance_row_needs_an_addressable_id() =>
        Assert.Contains("special_circumstances[0].id", ErrorsOf("""{"special_circumstances": [{"amount": "10"}]}""").Keys);

    [Fact]
    public void Duplicate_special_circumstance_ids_are_refused() =>
        Assert.Contains("special_circumstances[1].id", ErrorsOf("""{"special_circumstances": [{"id": "sc"}, {"id": "sc", "amount": "1"}]}""").Keys);

    [Fact]
    public void A_special_circumstance_amount_is_money() =>
        Assert.Contains("special_circumstances[0].amount", ErrorsOf("""{"special_circumstances": [{"id": "sc", "amount": "lots"}]}""").Keys);

    [Fact]
    public void More_than_two_vehicles_is_refused() =>
        Assert.Contains("vehicle_count", ErrorsOf("""{"vehicle_count": 3}""").Keys);

    private static IReadOnlyDictionary<string, string> ErrorsOf(string payload) => ErrorsOf(Body.Of(payload));

    private static IReadOnlyDictionary<string, string> ErrorsOf(JsonObject payload) =>
        Assert.Throws<FieldValidationException>(() => MeansTestInputRules.Parse(payload)).Fields;

    private static string? Text(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
}
