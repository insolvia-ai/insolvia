using Insolvia.Core.Debtors;
using Insolvia.Core.Expenses;
using Insolvia.Engines.Standards;
using Insolvia.Engines.Tests.Support;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_standards.py: the household
// jurisdiction derivation and the standards lookup, and their degrade paths.
public sealed class StandardsTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 4);
    private static readonly Debtor Debtor1 = Build.Debtor(FilingRole.Debtor1, "d-1", "FL", "Alachua County");

    // ── household_jurisdiction ──

    [Fact]
    public void No_debtors_resolves_to_no_jurisdiction() => Assert.Null(IrsStandards.HouseholdJurisdictionOf([], []));

    [Fact]
    public void A_debtor_with_no_address_resolves_to_no_jurisdiction() =>
        Assert.Null(IrsStandards.HouseholdJurisdictionOf([Build.Debtor(FilingRole.Debtor1, "d-1")], []));

    [Fact]
    public void A_state_with_no_county_resolves_to_no_jurisdiction() =>
        Assert.Null(IrsStandards.HouseholdJurisdictionOf([Build.Debtor(FilingRole.Debtor1, "d-1", "FL")], []));

    [Fact]
    public void Debtor_1_alone_is_a_household_of_one()
    {
        var result = IrsStandards.HouseholdJurisdictionOf([Debtor1], [])!;
        Assert.Equal("FL", result.State);
        Assert.Equal("Alachua County", result.County);
        Assert.Equal(1, result.HouseholdSize);
    }

    [Fact]
    public void A_filing_debtor_2_grows_the_household() =>
        Assert.Equal(2, IrsStandards.HouseholdJurisdictionOf([Debtor1, Build.Debtor(FilingRole.Debtor2, "d-2")], [])!.HouseholdSize);

    [Fact]
    public void A_non_filing_spouse_counts_toward_the_household() =>
        Assert.Equal(2, IrsStandards.HouseholdJurisdictionOf([Debtor1, Build.Debtor(FilingRole.NonFilingSpouse, "d-2")], [])!.HouseholdSize);

    [Fact]
    public void Only_dependents_living_with_the_debtor_count() =>
        Assert.Equal(2, IrsStandards.HouseholdJurisdictionOf([Debtor1], [Dependent(true), Dependent(false)])!.HouseholdSize);

    // ── resolve_standards ──

    [Fact]
    public void No_jurisdiction_reports_the_problem_and_nothing_resolves()
    {
        var result = IrsStandards.Resolve(AsOf, [], []);
        Assert.Null(result.State);
        Assert.Null(result.NationalAllowance);
        Assert.Null(result.HousingNonMortgage);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void A_supported_county_resolves_every_figure_with_no_problems()
    {
        var result = IrsStandards.Resolve(AsOf, [Debtor1], []);
        Assert.Empty(result.Problems);
        Assert.Equal("FL", result.State);
        Assert.Equal(1, result.HouseholdSize);
        Assert.False(string.IsNullOrEmpty(result.NationalReleaseId));
        Assert.False(string.IsNullOrEmpty(result.LocalReleaseId));
        Assert.All(
            new[]
            {
                result.NationalAllowance, result.OopHealthcareUnder65, result.HousingNonMortgage, result.HousingMortgageRent,
                result.TransportationPublicNational, result.TransportationOwnershipOneCar, result.TransportationOwnershipTwoCars,
                result.TransportationOperatingOneCar, result.TransportationOperatingTwoCars,
            },
            Assert.NotNull);
    }

    [Fact]
    public void An_unsupported_state_still_resolves_the_national_figures()
    {
        var result = IrsStandards.Resolve(AsOf, [Build.Debtor(FilingRole.Debtor1, "d-1", "ZZ", "Nowhere County")], []);
        Assert.NotNull(result.NationalAllowance);
        Assert.Null(result.HousingNonMortgage);
        Assert.Null(result.HousingMortgageRent);
        Assert.Null(result.TransportationOperatingOneCar);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void A_supported_state_with_an_unknown_county_reports_that_alone()
    {
        var result = IrsStandards.Resolve(AsOf, [Build.Debtor(FilingRole.Debtor1, "d-1", "FL", "Nowhere County")], []);
        Assert.NotNull(result.NationalAllowance);
        Assert.Null(result.HousingNonMortgage);
        Assert.Contains(result.Problems, problem => problem.Contains("Nowhere County", StringComparison.Ordinal));

        // str(KeyError) is the repr of its message, quotes and all — the problem prints it verbatim.
        Assert.Equal("\"FL: no housing standard for county 'Nowhere County'\"", result.Problems[0]);
    }

    [Fact]
    public void National_allowance_grows_with_household_size()
    {
        var solo = IrsStandards.Resolve(AsOf, [Debtor1], []);
        var pair = IrsStandards.Resolve(AsOf, [Debtor1, Build.Debtor(FilingRole.Debtor2, "d-2")], []);
        Assert.True(Py.Dec(pair.NationalAllowance!) > Py.Dec(solo.NationalAllowance!));
    }

    [Fact]
    public void Every_resolved_figure_is_a_two_decimal_money_string()
    {
        var result = IrsStandards.Resolve(AsOf, [Debtor1], []);
        foreach (var value in new[] { result.NationalAllowance, result.HousingNonMortgage, result.HousingMortgageRent })
        {
            Assert.NotNull(value);
            Assert.Equal(2, value.Split('.')[1].Length);
        }
    }

    private static DependentBody Dependent(bool livesWithDebtor) => new("h-1", "child", 9, livesWithDebtor);
}
