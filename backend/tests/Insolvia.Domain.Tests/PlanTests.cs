using System.Globalization;
using System.Text.Json.Nodes;
using Insolvia.Domain.Plans;

namespace Insolvia.Domain.Tests;

/// <summary>The Chapter 13 plan proposal, translated from test_plans.py.</summary>
public sealed class PlanTests
{
    public static TheoryData<PaymentSource> PaymentSources() => [.. Wire.Members<PaymentSource>()];

    public static TheoryData<UnsecuredTreatment> UnsecuredTreatments() => [.. Wire.Members<UnsecuredTreatment>()];

    public static TheoryData<SecuredTreatmentKind> SecuredTreatments() => [.. Wire.Members<SecuredTreatmentKind>()];

    [Fact]
    public void An_empty_payload_is_an_empty_plan()
    {
        var body = Body.Read<PlanBody>([]);
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
        Assert.Equal(term, Body.Read<PlanBody>(new JsonObject { ["termMonths"] = term }).TermMonths);

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(84)]
    public void A_term_past_five_years_is_refused(int term) =>
        Assert.Contains("termMonths", ErrorsOf(new JsonObject { ["termMonths"] = term }).Keys);

    [Fact]
    public void The_trustee_percentage_is_capped_at_ten()
    {
        Assert.Equal("10", Text(Body.Read<PlanBody>(Body.Of("""{"trusteePercentage": 10}""")).TrusteePercentage));
        Assert.Contains("trusteePercentage", ErrorsOf(Body.Of("""{"trusteePercentage": 10.5}""")).Keys);
    }

    [Theory]
    [MemberData(nameof(PaymentSources))]
    public void Every_payment_source_is_accepted(PaymentSource source) =>
        Assert.Equal(source, Body.Read<PlanBody>(new JsonObject { ["paymentSource"] = source.ToWire() }).PaymentSource);

    [Theory]
    [MemberData(nameof(UnsecuredTreatments))]
    public void Every_unsecured_treatment_is_accepted(UnsecuredTreatment treatment) =>
        Assert.Equal(treatment, Body.Read<PlanBody>(new JsonObject { ["unsecuredTreatment"] = treatment.ToWire() }).UnsecuredTreatment);

    [Theory]
    [MemberData(nameof(SecuredTreatments))]
    public void Every_secured_treatment_parses_whole(SecuredTreatmentKind treatment)
    {
        var body = Body.Read<PlanBody>(Body.Of($$"""
            {"securedTreatments": [{"id": "t1", "claimId": "claim-1", "treatment": "{{treatment.ToWire()}}", "arrearage": 1200, "interestRate": 7.25}]}
            """));
        var row = Assert.Single(body.SecuredTreatments);
        Assert.Equal(treatment, row.Treatment);
        Assert.Equal("1200", Text(row.Arrearage));
        Assert.Equal("7.25", Text(row.InterestRate));
    }

    [Fact]
    public void An_unknown_secured_treatment_is_refused() =>
        Assert.Contains("securedTreatments[0].treatment", ErrorsOf(Body.Of("""{"securedTreatments": [{"id": "t1", "treatment": "strip_off"}]}""")).Keys);

    [Fact]
    public void One_claim_cannot_carry_two_treatments() =>
        Assert.Contains("securedTreatments[1].claimId", ErrorsOf(Body.Of("""
            {"securedTreatments": [{"id": "t1", "claimId": "c", "treatment": "surrender"}, {"id": "t2", "claimId": "c", "treatment": "cramdown"}]}
            """)).Keys);

    [Fact]
    public void A_row_without_an_id_is_refused() =>
        Assert.Contains("lumpSums[0].id", ErrorsOf(Body.Of("""{"lumpSums": [{"month": 3, "amount": 100}]}""")).Keys);

    [Fact]
    public void Two_steps_cannot_start_in_one_month() =>
        Assert.Contains("stepPayments[1].startMonth", ErrorsOf(Body.Of("""
            {"stepPayments": [{"id": "s1", "startMonth": 13, "monthlyPayment": 500}, {"id": "s2", "startMonth": 13, "monthlyPayment": 600}]}
            """)).Keys);

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void A_lump_sum_must_fall_inside_the_plan(int month) =>
        Assert.Contains("lumpSums[0].month", ErrorsOf(Body.Of($$"""{"lumpSums": [{"id": "l1", "month": {{month}}}]}""")).Keys);

    private static IReadOnlyDictionary<string, string> ErrorsOf(JsonObject payload) =>
        Assert.Throws<FieldValidationException>(() => Body.Read<PlanBody>(payload)).Fields;

    private static string? Text(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
}
