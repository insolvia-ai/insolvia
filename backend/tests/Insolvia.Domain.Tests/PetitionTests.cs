using Insolvia.Domain.Petitions;

namespace Insolvia.Domain.Tests;

/// <summary>B101's entities, translated from test_petitions.py.</summary>
public sealed class PetitionTests
{
    [Fact]
    public void A_full_petition_parses()
    {
        var body = Body.Read<PetitionBody>(Body.Of("""
            {"feeHandling": "waiver", "smallBusinessStatus": "chapter_11_subchapter_v",
             "hazardousProperty": {"description": "Chemical drums", "whyImmediate": "Corroding", "address": {"line1": "9 Dock Rd", "state": "FL"}},
             "debtCharacter": "other", "debtCharacterOther": "Mixed personal guarantees", "estimatedCreditors": "50_99"}
            """));
        Assert.Equal(FeeHandling.Waiver, body.FeeHandling);
        Assert.Equal("FL", body.HazardousProperty.Address.State);
        Assert.Equal("Mixed personal guarantees", body.DebtCharacterOther);
        Assert.Equal(EstimatedCreditors.Band50To99, body.EstimatedCreditors);
    }

    [Theory]
    [InlineData("""{"feeHandling": "monthly"}""", "feeHandling")]
    [InlineData("""{"smallBusinessStatus": "yes"}""", "smallBusinessStatus")]
    [InlineData("""{"debtCharacter": "personal"}""", "debtCharacter")]
    [InlineData("""{"estimatedCreditors": "1-49"}""", "estimatedCreditors")]
    [InlineData("""{"estimatedAssets": "plenty"}""", "estimatedAssets")]
    [InlineData("""{"hazardousProperty": "propane"}""", "hazardousProperty")]
    public void Petition_enums_are_closed(string payload, string badField) =>
        Assert.Contains(badField, Assert.Throws<FieldValidationException>(() => Body.Read<PetitionBody>(Body.Of(payload))).Fields.Keys);

    [Fact]
    public void Expected_filing_date_is_a_form_date()
    {
        Assert.Equal(new DateOnly(2026, 6, 15), Body.Read<PetitionBody>(Body.Of("""{"expectedFilingDate": "2026-06-15"}""")).ExpectedFilingDate);
        Assert.Contains("expectedFilingDate", Assert.Throws<FieldValidationException>(() => Body.Read<PetitionBody>(Body.Of("""{"expectedFilingDate": "06/15/2026"}"""))).Fields.Keys);
    }

    [Fact]
    public void Prior_case_dates_are_form_dates() =>
        Assert.Contains("filedOn", Assert.Throws<FieldValidationException>(() => Body.Read<PriorCaseBody>(Body.Of("""{"filedOn": "03/04/2019"}"""))).Fields.Keys);

    [Fact]
    public void Sole_proprietorship_business_type_is_closed() =>
        Assert.Contains("businessType", Assert.Throws<FieldValidationException>(() => Body.Read<SoleProprietorshipBody>(Body.Of("""{"businessType": "restaurant"}"""))).Fields.Keys);

    [Fact]
    public void Filing_professional_role_and_bar_state()
    {
        Assert.Equal(FilingProfessionalRole.BankruptcyPetitionPreparer, Body.Read<FilingProfessionalBody>(Body.Of("""{"role": "bankruptcy_petition_preparer", "barState": "FL"}""")).Role);
        Assert.Equal(["role"], Assert.Throws<FieldValidationException>(() => Body.Read<FilingProfessionalBody>(Body.Of("""{"role": "notary"}"""))).Fields.Keys);
        Assert.Equal(["barState"], Assert.Throws<FieldValidationException>(() => Body.Read<FilingProfessionalBody>(Body.Of("""{"barState": "Florida"}"""))).Fields.Keys);
    }

    [Fact]
    public void Filing_professional_compensation_fields_parse()
    {
        var body = Body.Read<FilingProfessionalBody>(Body.Of("""
            {"role": "attorney", "compensationAgreed": 1500, "compensationReceived": 1000.5, "compensationSourcePaid": "debtor",
             "compensationSourceToBePaid": "other", "compensationSourceToBePaidOther": "A relative", "compensationShared": false,
             "servicesOther": "Reaffirmation negotiations.", "servicesExcluded": "Adversary proceedings."}
            """));
        Assert.Equal(1500.00m, body.CompensationAgreed);
        Assert.Equal(1000.5m, body.CompensationReceived);
        Assert.Equal(CompensationSource.Debtor, body.CompensationSourcePaid);
        Assert.Equal(CompensationSource.Other, body.CompensationSourceToBePaid);
        Assert.Equal("A relative", body.CompensationSourceToBePaidOther);
        Assert.False(body.CompensationShared);
        Assert.Equal("Adversary proceedings.", body.ServicesExcluded);
        Assert.Null(Body.Read<FilingProfessionalBody>([]).CompensationAgreed);
    }

    [Theory]
    [InlineData("""{"compensationAgreed": "1500"}""", "compensationAgreed")]
    [InlineData("""{"compensationReceived": -1}""", "compensationReceived")]
    [InlineData("""{"compensationSourcePaid": "trustee"}""", "compensationSourcePaid")]
    [InlineData("""{"compensationSourceToBePaid": "Debtor"}""", "compensationSourceToBePaid")]
    [InlineData("""{"compensationShared": "no"}""", "compensationShared")]
    public void Filing_professional_compensation_fields_are_validated(string payload, string badField) =>
        Assert.Equal([badField], Assert.Throws<FieldValidationException>(() => Body.Read<FilingProfessionalBody>(Body.Of(payload))).Fields.Keys);

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
