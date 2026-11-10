using Insolvia.Domain.Engines.Standards;

namespace Insolvia.Api.Analysis;

/// <summary>
/// The IRS National and Local Standards for the case's jurisdiction and
/// household. Money is a decimal string; a figure that could not resolve is
/// null, so the screen knows which box to label missing.
/// </summary>
/// <param name="AsOf">The date resolved as of, <c>YYYY-MM-DD</c>.</param>
/// <param name="State">The state; null when unknown.</param>
/// <param name="County">The county; null when unknown.</param>
/// <param name="HouseholdSize">The household size; null when unknown.</param>
/// <param name="JurisdictionSource">Where the state and county came from.</param>
/// <param name="NationalStandards">The National Standards figures.</param>
/// <param name="LocalStandards">The Local Standards figures.</param>
/// <param name="Problems">What could not resolve, for a person.</param>
public sealed record StandardsView(
    string AsOf,
    string? State,
    string? County,
    int? HouseholdSize,
    string? JurisdictionSource,
    NationalStandardsView NationalStandards,
    LocalStandardsView LocalStandards,
    IReadOnlyList<string> Problems)
{
    /// <summary>The view of <paramref name="result"/>.</summary>
    /// <param name="result">The resolved standards.</param>
    /// <returns>The view.</returns>
    public static StandardsView Of(StandardsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
            WireFormat.Date(result.AsOf),
            result.State,
            result.County,
            result.HouseholdSize,
            result.JurisdictionSource,
            new(result.NationalReleaseId, result.NationalAllowance, result.OopHealthcareUnder65, result.OopHealthcare65AndOlder),
            new(
                result.LocalReleaseId,
                result.HousingNonMortgage,
                result.HousingMortgageRent,
                result.TransportationPublicNational,
                result.TransportationOwnershipOneCar,
                result.TransportationOwnershipTwoCars,
                result.TransportationOperatingOneCar,
                result.TransportationOperatingTwoCars),
            result.Problems);
    }
}

/// <summary>The IRS National Standards.</summary>
/// <param name="ReleaseId">The release used.</param>
/// <param name="Allowance">Food, clothing and other items, monthly.</param>
/// <param name="OopHealthcareUnder65">Out-of-pocket health care per person under 65, monthly.</param>
/// <param name="OopHealthcare65AndOlder">Out-of-pocket health care per person 65 or older, monthly.</param>
public sealed record NationalStandardsView(string? ReleaseId, string? Allowance, string? OopHealthcareUnder65, string? OopHealthcare65AndOlder);

/// <summary>The IRS Local Standards.</summary>
/// <param name="ReleaseId">The release used.</param>
/// <param name="HousingNonMortgage">Housing and utilities, non-mortgage, monthly.</param>
/// <param name="HousingMortgageRent">Housing and utilities, mortgage or rent, monthly.</param>
/// <param name="TransportationPublicNational">Public transportation, monthly.</param>
/// <param name="TransportationOwnershipOneCar">Ownership costs, one car.</param>
/// <param name="TransportationOwnershipTwoCars">Ownership costs, two cars.</param>
/// <param name="TransportationOperatingOneCar">Operating costs, one car.</param>
/// <param name="TransportationOperatingTwoCars">Operating costs, two cars.</param>
public sealed record LocalStandardsView(
    string? ReleaseId,
    string? HousingNonMortgage,
    string? HousingMortgageRent,
    string? TransportationPublicNational,
    string? TransportationOwnershipOneCar,
    string? TransportationOwnershipTwoCars,
    string? TransportationOperatingOneCar,
    string? TransportationOperatingTwoCars);
