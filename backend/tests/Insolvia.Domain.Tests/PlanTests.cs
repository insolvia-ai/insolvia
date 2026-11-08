using System.Globalization;
using System.Text.Json.Nodes;
using Insolvia.Core.Fields;
using Insolvia.Core.Plans;

namespace Insolvia.Core.Tests;

/// <summary>The Chapter 13 plan proposal, translated from test_plans.py.</summary>
public sealed class PlanTests
{
    public static TheoryData<PaymentSource> PaymentSources() => [.. Wire.Members<PaymentSource>()];

    public static TheoryData<UnsecuredTreatment> UnsecuredTreatments() => [.. Wire.Members<UnsecuredTreatment>()];

    public static TheoryData<SecuredTreatmentKind> SecuredTreatments() => [.. Wire.Members<SecuredTreatmentKind>()];

    [Fact]
    public void An_empty_payload_is_an_empty_plan()
    {
        var body = PlanRules.Parse([]);
        Assert.Null(body.TermMonths);
        Assert.Null(body.PaymentSource);
        Assert.Empty(body.SecuredTreatments);
        Assert.Empty(body.StepPayments);
        Assert.Empty(body.LumpSums);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(36)]
    [InlineData(60)]
    public void A_term_inside_five_years_is_accepted(int term) =>
        Assert.Equal(term, PlanRules.Parse(new JsonObject { ["term_months"] = term }).TermMonths);

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(84)]
    public void A_term_past_five_years_is_refused(int term) =>
        Assert.Contains("term_months", ErrorsOf(new JsonObject { ["term_months"] = term }).Keys);

    [Fact]
    public void The_trustee_percentage_is_capped_at_ten()
    {
        Assert.Equal("10.00", Text(PlanRules.Parse(Body.Of("""{"trustee_percentage": "10"}""")).TrusteePercentage));
        Assert.Contains("trustee_percentage", ErrorsOf(Body.Of("""{"trustee_percentage": "10.5"}""")).Keys);
    }

    [Theory]
    [InlineData("8.5", "8.50")]
    [InlineData("8.500", "8.50")]
    [InlineData("6.375", "6.375")]
    [InlineData("0", "0.00")]
    public void A_percentage_is_stored_canonically(string given, string stored)
    {
        var errors = new Dictionary<string, string>();
        Assert.Equal(stored, Text(FieldParsers.Percentage(JsonValue.Create(given), "rate", errors)));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("8.5")]
    [InlineData("\"-1\"")]
    [InlineData("\"100.01\"")]
    [InlineData("\"6.3755\"")]
    [InlineData("\"eight\"")]
    [InlineData("\"NaN\"")]
    public void A_malformed_percentage_is_refused(string given)
    {
        var errors = new Dictionary<string, string>();
        Assert.Null(FieldParsers.Percentage(Body.Value(given), "rate", errors));
        Assert.Contains("rate", errors.Keys);
    }

    [Theory]
    [MemberData(nameof(PaymentSources))]
    public void Every_payment_source_is_accepted(PaymentSource source) =>
        Assert.Equal(source, PlanRules.Parse(new JsonObject { ["payment_source"] = source.ToWire() }).PaymentSource);

    [Theory]
    [MemberData(nameof(UnsecuredTreatments))]
    public void Every_unsecured_treatment_is_accepted(UnsecuredTreatment treatment) =>
        Assert.Equal(treatment, PlanRules.Parse(new JsonObject { ["unsecured_treatment"] = treatment.ToWire() }).UnsecuredTreatment);

    [Theory]
    [MemberData(nameof(SecuredTreatments))]
    public void Every_secured_treatment_parses_whole(SecuredTreatmentKind treatment)
    {
        var body = PlanRules.Parse(Body.Of($$"""
            {"secured_treatments": [{"id": "t1", "claim_id": "claim-1", "treatment": "{{treatment.ToWire()}}", "arrearage": "1200", "interest_rate": "7.25"}]}
            """));
        var row = Assert.Single(body.SecuredTreatments);
        Assert.Equal(treatment, row.Treatment);
        Assert.Equal("1200.00", Text(row.Arrearage));
        Assert.Equal("7.25", Text(row.InterestRate));
    }

    [Fact]
    public void An_unknown_secured_treatment_is_refused() =>
        Assert.Contains("secured_treatments[0].treatment", ErrorsOf(Body.Of("""{"secured_treatments": [{"id": "t1", "treatment": "strip_off"}]}""")).Keys);

    [Fact]
    public void One_claim_cannot_carry_two_treatments() =>
        Assert.Contains("secured_treatments[1].claim_id", ErrorsOf(Body.Of("""
            {"secured_treatments": [{"id": "t1", "claim_id": "c", "treatment": "surrender"}, {"id": "t2", "claim_id": "c", "treatment": "cramdown"}]}
            """)).Keys);

    [Fact]
    public void A_row_without_an_id_is_refused() =>
        Assert.Contains("lump_sums[0].id", ErrorsOf(Body.Of("""{"lump_sums": [{"month": 3, "amount": "100"}]}""")).Keys);

    [Fact]
    public void Two_steps_cannot_start_in_one_month() =>
        Assert.Contains("step_payments[1].start_month", ErrorsOf(Body.Of("""
            {"step_payments": [{"id": "s1", "start_month": 13, "monthly_payment": "500"}, {"id": "s2", "start_month": 13, "monthly_payment": "600"}]}
            """)).Keys);

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void A_lump_sum_must_fall_inside_the_plan(int month) =>
        Assert.Contains("lump_sums[0].month", ErrorsOf(Body.Of($$"""{"lump_sums": [{"id": "l1", "month": {{month}}}]}""")).Keys);

    private static IReadOnlyDictionary<string, string> ErrorsOf(JsonObject payload) =>
        Assert.Throws<FieldValidationException>(() => PlanRules.Parse(payload)).Fields;

    private static string? Text(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
}
