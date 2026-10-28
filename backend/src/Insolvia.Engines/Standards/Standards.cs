using Insolvia.Core.Debtors;
using Insolvia.Core.Expenses;
using Insolvia.Engines.Regulatory;

namespace Insolvia.Engines.Standards;

/// <summary>Where to look the standards up, and how many people they cover.</summary>
public sealed record HouseholdJurisdiction(string State, string County, int HouseholdSize, string Source);

/// <summary>
/// What the workbench shows beside Schedule J's health-care and
/// transportation lines. A null figure means <see cref="Problems"/> says why.
/// </summary>
public sealed record StandardsResult(
    DateOnly AsOf,
    string? State,
    string? County,
    int? HouseholdSize,
    string? JurisdictionSource,
    string? NationalReleaseId,
    string? NationalAllowance,
    string? OopHealthcareUnder65,
    string? OopHealthcare65AndOlder,
    string? LocalReleaseId,
    string? HousingNonMortgage,
    string? HousingMortgageRent,
    string? TransportationPublicNational,
    string? TransportationOwnershipOneCar,
    string? TransportationOwnershipTwoCars,
    string? TransportationOperatingOneCar,
    string? TransportationOperatingTwoCars,
    IReadOnlyList<string> Problems);

/// <summary>
/// The IRS National and Local Standards for a case's jurisdiction and
/// household (from <c>core/standards.py</c>, issue #348) — NOT the means
/// test: the best facts already on file, read-only, every failed lookup a
/// problem rather than a guess.
/// </summary>
public static class IrsStandards
{
    /// <summary>Debtor 1's residence state/county and a household counted from the filers and co-resident dependents; null when too early.</summary>
    public static HouseholdJurisdiction? HouseholdJurisdictionOf(IReadOnlyList<Debtor> debtors, IReadOnlyList<DependentBody> dependents)
    {
        ArgumentNullException.ThrowIfNull(debtors);
        ArgumentNullException.ThrowIfNull(dependents);
        var debtor1 = debtors.FirstOrDefault(debtor => debtor.FilingRole == FilingRole.Debtor1);
        if (debtor1 is null)
        {
            return null;
        }

        var address = debtor1.Body.ResidenceAddress;
        if (string.IsNullOrEmpty(address.State) || string.IsNullOrEmpty(address.County))
        {
            return null;
        }

        var filers = debtors.Count(debtor => debtor.FilingRole is FilingRole.Debtor1 or FilingRole.Debtor2 or FilingRole.NonFilingSpouse);
        var livingWith = dependents.Count(dependent => dependent.LivesWithDebtor == true);
        return new HouseholdJurisdiction(
            address.State,
            address.County,
            Math.Max(filers, 1) + livingWith,
            $"Debtor 1's residence address, plus {livingWith} dependent(s) recorded as living with the debtor");
    }

    /// <summary>
    /// The whole read, pure. Never throws for a case too early to answer;
    /// throws <see cref="EngineLookupException"/> only when the registry has
    /// no release for <paramref name="asOf"/>.
    /// </summary>
    public static StandardsResult Resolve(DateOnly asOf, IReadOnlyList<Debtor> debtors, IReadOnlyList<DependentBody> dependents)
    {
        var problems = new List<string>();
        var jurisdiction = HouseholdJurisdictionOf(debtors, dependents);
        if (jurisdiction is null)
        {
            problems.Add("Debtor 1's residence address needs a state and county before the IRS standards can be looked up (B101 line 5).");
        }

        var (nationalRelease, national) = UstData.NationalStandards(asOf);
        var (localRelease, local) = UstData.LocalStandards(asOf);

        string? nationalAllowance = null;
        string? housingNonMortgage = null;
        string? housingMortgageRent = null;
        string? operatingOne = null;
        string? operatingTwo = null;
        if (jurisdiction is not null)
        {
            nationalAllowance = Money(national.Allowance(jurisdiction.HouseholdSize));
            try
            {
                var row = local.HousingFor(jurisdiction.State, jurisdiction.County);
                housingNonMortgage = Money(row.NonMortgageFor(jurisdiction.HouseholdSize));
                housingMortgageRent = Money(row.MortgageRentFor(jurisdiction.HouseholdSize));
            }
            catch (EngineKeyException error)
            {
                problems.Add(Py.Str(error));
            }

            try
            {
                var operating = local.Transportation.OperatingCostsFor(jurisdiction.State, jurisdiction.County);
                operatingOne = operating.OneCar;
                operatingTwo = operating.TwoCars;
            }
            catch (EngineKeyException error)
            {
                problems.Add(Py.Str(error));
            }
        }

        return new StandardsResult(
            AsOf: asOf,
            State: jurisdiction?.State,
            County: jurisdiction?.County,
            HouseholdSize: jurisdiction?.HouseholdSize,
            JurisdictionSource: jurisdiction?.Source,
            NationalReleaseId: nationalRelease.ReleaseId,
            NationalAllowance: nationalAllowance,
            OopHealthcareUnder65: national.OopHealthcareUnder65,
            OopHealthcare65AndOlder: national.OopHealthcare65AndOlder,
            LocalReleaseId: localRelease.ReleaseId,
            HousingNonMortgage: housingNonMortgage,
            HousingMortgageRent: housingMortgageRent,
            TransportationPublicNational: local.Transportation.PublicTransportationNational,
            TransportationOwnershipOneCar: local.Transportation.OwnershipCosts.OneCar,
            TransportationOwnershipTwoCars: local.Transportation.OwnershipCosts.TwoCars,
            TransportationOperatingOneCar: operatingOne,
            TransportationOperatingTwoCars: operatingTwo,
            Problems: problems);
    }

    // `_money`: ROUND_HALF_UP.
    private static string Money(decimal value) => Py.Money(value, PyRounding.HalfUp);
}
