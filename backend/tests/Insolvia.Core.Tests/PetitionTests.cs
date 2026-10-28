using Insolvia.Core.Petitions;

namespace Insolvia.Core.Tests;

/// <summary>B101's entities, translated from test_petitions.py.</summary>
public sealed class PetitionTests
{
    [Fact]
    public void A_full_petition_parses()
    {
        var body = PetitionRules.ParsePetition(Body.Of("""
            {"fee_handling": "waiver", "small_business_status": "chapter_11_subchapter_v",
             "hazardous_property": {"description": "Chemical drums", "why_immediate": "Corroding", "address": {"line1": "9 Dock Rd", "state": "FL"}},
             "debt_character": "other", "debt_character_other": "Mixed personal guarantees", "estimated_creditors": "50_99"}
            """));
        Assert.Equal(FeeHandling.Waiver, body.FeeHandling);
        Assert.Equal("FL", body.HazardousProperty.Address.State);
        Assert.Equal("Mixed personal guarantees", body.DebtCharacterOther);
        Assert.Equal(EstimatedCreditors.Band50To99, body.EstimatedCreditors);
    }

    [Theory]
    [InlineData("""{"fee_handling": "monthly"}""", "fee_handling")]
    [InlineData("""{"small_business_status": "yes"}""", "small_business_status")]
    [InlineData("""{"debt_character": "personal"}""", "debt_character")]
    [InlineData("""{"estimated_creditors": "1-49"}""", "estimated_creditors")]
    [InlineData("""{"estimated_assets": "plenty"}""", "estimated_assets")]
    [InlineData("""{"hazardous_property": "propane"}""", "hazardous_property")]
    public void Petition_enums_are_closed(string payload, string badField) =>
        Assert.Contains(badField, Assert.Throws<FieldValidationException>(() => PetitionRules.ParsePetition(Body.Of(payload))).Fields.Keys);

    [Fact]
    public void Expected_filing_date_is_a_form_date()
    {
        Assert.Equal(new DateOnly(2026, 6, 15), PetitionRules.ParsePetition(Body.Of("""{"expected_filing_date": "2026-06-15"}""")).ExpectedFilingDate);
        Assert.Contains("expected_filing_date", Assert.Throws<FieldValidationException>(() => PetitionRules.ParsePetition(Body.Of("""{"expected_filing_date": "06/15/2026"}"""))).Fields.Keys);
    }

    [Fact]
    public void Prior_case_dates_are_form_dates() =>
        Assert.Contains("filed_on", Assert.Throws<FieldValidationException>(() => PetitionRules.ParsePriorCase(Body.Of("""{"filed_on": "03/04/2019"}"""))).Fields.Keys);

    [Fact]
    public void Sole_proprietorship_business_type_is_closed() =>
        Assert.Contains("business_type", Assert.Throws<FieldValidationException>(() => PetitionRules.ParseSoleProprietorship(Body.Of("""{"business_type": "restaurant"}"""))).Fields.Keys);

    [Fact]
    public void Filing_professional_role_and_bar_state()
    {
        Assert.Equal(FilingProfessionalRole.BankruptcyPetitionPreparer, PetitionRules.ParseFilingProfessional(Body.Of("""{"role": "bankruptcy_petition_preparer", "bar_state": "FL"}""")).Role);
        var error = Assert.Throws<FieldValidationException>(() => PetitionRules.ParseFilingProfessional(Body.Of("""{"role": "notary", "bar_state": "Florida"}""")));
        Assert.Equal(["bar_state", "role"], error.Fields.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Filing_professional_compensation_fields_parse()
    {
        var body = PetitionRules.ParseFilingProfessional(Body.Of("""
            {"role": "attorney", "compensation_agreed": "1500", "compensation_received": "1000.5", "compensation_source_paid": "debtor",
             "compensation_source_to_be_paid": "other", "compensation_source_to_be_paid_other": "A relative", "compensation_shared": false,
             "services_other": "Reaffirmation negotiations.", "services_excluded": "Adversary proceedings."}
            """));
        Assert.Equal(1500.00m, body.CompensationAgreed);
        Assert.Equal("1000.50", body.CompensationReceived!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(CompensationSource.Debtor, body.CompensationSourcePaid);
        Assert.Equal(CompensationSource.Other, body.CompensationSourceToBePaid);
        Assert.Equal("A relative", body.CompensationSourceToBePaidOther);
        Assert.False(body.CompensationShared);
        Assert.Equal("Adversary proceedings.", body.ServicesExcluded);
        Assert.Null(PetitionRules.ParseFilingProfessional([]).CompensationAgreed);
    }

    [Theory]
    [InlineData("""{"compensation_agreed": 1500}""", "compensation_agreed")]
    [InlineData("""{"compensation_received": "-1"}""", "compensation_received")]
    [InlineData("""{"compensation_source_paid": "trustee"}""", "compensation_source_paid")]
    [InlineData("""{"compensation_source_to_be_paid": "Debtor"}""", "compensation_source_to_be_paid")]
    [InlineData("""{"compensation_shared": "no"}""", "compensation_shared")]
    public void Filing_professional_compensation_fields_are_validated(string payload, string badField) =>
        Assert.Equal([badField], Assert.Throws<FieldValidationException>(() => PetitionRules.ParseFilingProfessional(Body.Of(payload))).Fields.Keys);

    [Fact]
    public void The_petition_bands_keep_their_wire_strings()
    {
        Assert.Equal(
            ["1_49", "50_99", "100_199", "200_999", "1000_5000", "5001_10000", "10001_25000", "25001_50000", "50001_100000", "more_than_100000"],
            Wire.Names<EstimatedCreditors>());
        Assert.Equal(12, Wire.Names<EstimatedDollars>().Count);
        Assert.Equal("more_than_50000000000", Wire.Names<EstimatedDollars>()[^1]);
    }
}
