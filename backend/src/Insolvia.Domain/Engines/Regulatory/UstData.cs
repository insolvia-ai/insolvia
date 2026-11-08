using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Insolvia.Engines.Regulatory.ReleaseParsing;

namespace Insolvia.Engines.Regulatory;

// The UST means-testing datasets: loader and resolution for the `ust/*` series
// (from core/ust_data.py). The local-standards payload is scoped to the launch
// states (ADR 0017); the medians table keeps every state and territory.
// Resolution follows effective-dating.md exactly.

/// <summary>A UST payload — one of the four tables below.</summary>
public abstract record UstPayload;

/// <summary>
/// Annual median family income by state/territory for household sizes 1-4;
/// above 4 the excess-person figure is added per person.
/// </summary>
public sealed record MedianIncomeTable(IReadOnlyDictionary<string, IReadOnlyList<string>> AnnualMedians, string ExcessPersonAnnualAddition) : UstPayload
{
    /// <summary>B122A-1 line 13's lookup. <see cref="EngineKeyException"/> for an unknown state, <see cref="EngineValueException"/> for a household below one.</summary>
    public decimal AnnualMedian(string state, int householdSize)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (householdSize < 1)
        {
            throw new EngineValueException($"household size {householdSize} is not positive");
        }

        if (!AnnualMedians.TryGetValue(state.ToUpperInvariant(), out var row))
        {
            throw new EngineKeyException($"the median income table has no row for {Exact.Repr(state)}");
        }

        if (householdSize <= 4)
        {
            return UstData.Dec(row[householdSize - 1]);
        }

        return UstData.Dec(row[3]) + (UstData.Dec(ExcessPersonAnnualAddition) * (householdSize - 4));
    }
}

/// <summary>One published component line of the National Standards total.</summary>
public sealed record NationalStandardComponent(string Item, IReadOnlyList<string> Monthly);

/// <summary>B122A-2 lines 6-7's tables, and line 30's food-and-clothing cap.</summary>
public sealed record NationalStandards(
    IReadOnlyList<string> MonthlyAllowances,
    string EachAdditionalPerson,
    IReadOnlyList<NationalStandardComponent> Components,
    IReadOnlyList<string> FoodAndClothing,
    string FoodAndClothingEachAdditionalPerson,
    IReadOnlyList<string> AdditionalFoodClothingCap,
    string AdditionalFoodClothingCapEachAdditionalPerson,
    string OopHealthcareUnder65,
    string OopHealthcare65AndOlder) : UstPayload
{
    /// <summary>The line 6 monthly allowance for a household of this size.</summary>
    public decimal Allowance(int householdSize)
    {
        if (householdSize < 1)
        {
            throw new EngineValueException($"household size {householdSize} is not positive");
        }

        if (householdSize <= 4)
        {
            return UstData.Dec(MonthlyAllowances[householdSize - 1]);
        }

        return UstData.Dec(MonthlyAllowances[3]) + (UstData.Dec(EachAdditionalPerson) * (householdSize - 4));
    }

    /// <summary>Line 30's cap for this household.</summary>
    public decimal AdditionalFoodClothingCapFor(int householdSize)
    {
        if (householdSize < 1)
        {
            throw new EngineValueException($"household size {householdSize} is not positive");
        }

        if (householdSize <= 4)
        {
            return UstData.Dec(AdditionalFoodClothingCap[householdSize - 1]);
        }

        return UstData.Dec(AdditionalFoodClothingCap[3]) + (UstData.Dec(AdditionalFoodClothingCapEachAdditionalPerson) * (householdSize - 4));
    }

    /// <summary>The line 7 monthly allowance: per-person amounts by age band.</summary>
    public decimal OopHealthcare(int under65, int over65)
    {
        if (under65 < 0 || over65 < 0)
        {
            throw new EngineValueException("household member counts cannot be negative");
        }

        return (UstData.Dec(OopHealthcareUnder65) * under65) + (UstData.Dec(OopHealthcare65AndOlder) * over65);
    }
}

/// <summary>One county's housing and utilities allowances, household sizes 1,2,3,4,5+.</summary>
public sealed record CountyHousing(string Fips, string County, IReadOnlyList<string> NonMortgage, IReadOnlyList<string> MortgageRent)
{
    /// <summary>B122A-2 line 8.</summary>
    public decimal NonMortgageFor(int householdSize) => Pick(NonMortgage, householdSize);

    /// <summary>B122A-2 line 9a.</summary>
    public decimal MortgageRentFor(int householdSize) => Pick(MortgageRent, householdSize);

    private static decimal Pick(IReadOnlyList<string> values, int householdSize)
    {
        if (householdSize < 1)
        {
            throw new EngineValueException($"household size {householdSize} is not positive");
        }

        return UstData.Dec(values[Math.Min(householdSize, 5) - 1]);
    }
}

/// <summary>A one-or-two-vehicle cost row.</summary>
public record VehicleCosts(string OneCar, string TwoCars)
{
    /// <summary>The published column for one or two vehicles.</summary>
    public decimal ForVehicles(int vehicleCount) => vehicleCount switch
    {
        1 => UstData.Dec(OneCar),
        2 => UstData.Dec(TwoCars),
        _ => throw new EngineValueException($"the table has no column for {vehicleCount} vehicles"),
    };
}

/// <summary>An MSA's operating-cost override, with the counties it covers.</summary>
public sealed record MsaOperatingCosts(string OneCar, string TwoCars, string State, IReadOnlyList<string> Counties) : VehicleCosts(OneCar, TwoCars);

/// <summary>The transportation tables (B122A-2 lines 11-14).</summary>
public sealed record Transportation(
    string PublicTransportationNational,
    VehicleCosts OwnershipCosts,
    IReadOnlyDictionary<string, string> OperatingCostsRegion,
    IReadOnlyDictionary<string, VehicleCosts> OperatingCosts,
    IReadOnlyList<MsaOperatingCosts> OperatingCostsMsa)
{
    /// <summary>The operating-costs row for a county: its MSA's override, else its state's Census region.</summary>
    public VehicleCosts OperatingCostsFor(string state, string county)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = UstData.NormalizeCounty(county);
        var upper = state.ToUpperInvariant();
        foreach (var msa in OperatingCostsMsa)
        {
            if (msa.State == upper && msa.Counties.Any(name => UstData.NormalizeCounty(name) == normalized))
            {
                return msa;
            }
        }

        if (!OperatingCostsRegion.TryGetValue(state.ToUpperInvariant(), out var region))
        {
            throw new EngineKeyException($"no Census region is recorded for state {Exact.Repr(state)}");
        }

        return OperatingCosts[region];
    }
}

/// <summary>The Local Standards payload, scoped to the launch states.</summary>
public sealed record LocalStandards(IReadOnlyList<string> States, IReadOnlyDictionary<string, IReadOnlyList<CountyHousing>> Housing, Transportation Transportation) : UstPayload
{
    /// <summary>The county row B122A-2 lines 8-9 read; <see cref="EngineKeyException"/> when it is not in the table.</summary>
    public CountyHousing HousingFor(string state, string county)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Housing.TryGetValue(state.ToUpperInvariant(), out var rows))
        {
            throw new EngineKeyException(
                $"the local standards carry no housing table for {Exact.Repr(state)} (launch states: {string.Join(", ", States)})");
        }

        var normalized = UstData.NormalizeCounty(county);
        return rows.FirstOrDefault(row => UstData.NormalizeCounty(row.County) == normalized)
            ?? throw new EngineKeyException($"{state}: no housing standard for county {Exact.Repr(county)}");
    }
}

/// <summary>The UST's Chapter 13 administrative expense multipliers per judicial district (B122A-2 line 36).</summary>
public sealed record Ch13AdminMultipliers(IReadOnlyDictionary<string, string> Multipliers) : UstPayload
{
    /// <summary>The fraction for a case's district, by the UST's spelling or the courts'.</summary>
    public decimal MultiplierFor(string district)
    {
        var cleaned = string.Join(' ', Core.TextRules.SplitWhitespace(district));
        Multipliers.TryGetValue(cleaned, out var found);
        if (found is null && cleaned != "District of Columbia")
        {
            var stripped = string.Join(' ', Core.TextRules.SplitWhitespace(cleaned.Replace("District of", string.Empty, StringComparison.Ordinal)));
            Multipliers.TryGetValue(stripped, out found);
        }

        if (found is null)
        {
            throw new EngineKeyException($"the Chapter 13 multiplier table has no district {Exact.Repr(district)}");
        }

        return UstData.Dec(found);
    }
}

/// <summary>One immutable release of a UST series.</summary>
public sealed record UstRelease(
    string SeriesId,
    DateOnly EffectiveDate,
    int Sequence,
    string SourceUrl,
    DateOnly? SourcePublished,
    string? SourceSha256,
    string Notes,
    Verification Verification,
    IReadOnlyList<Source> Sources,
    UstPayload Payload) : IRelease
{
    /// <summary><c>series@date[+sequence]</c>.</summary>
    public string ReleaseId => Releases.Id(SeriesId, EffectiveDate, Sequence);
}

/// <summary>The UST means-testing datasets: load, validate, resolve (Python's <c>core/ust_data.py</c>).</summary>
public static partial class UstData
{
    /// <summary>§ 707(b)(7)'s median table.</summary>
    public const string MedianIncomeSeries = "ust/census-median-family-income";

    /// <summary>The IRS National Standards.</summary>
    public const string NationalStandardsSeries = "ust/irs-national-standards";

    /// <summary>The IRS Local Standards.</summary>
    public const string LocalStandardsSeries = "ust/irs-local-standards";

    /// <summary>The Chapter 13 administrative multipliers.</summary>
    public const string Ch13MultipliersSeries = "ust/ch13-admin-multipliers";

    private static readonly Dictionary<string, string> SeriesPayloads = new(StringComparer.Ordinal)
    {
        [MedianIncomeSeries] = "census-median-family-income",
        [NationalStandardsSeries] = "irs-national-standards",
        [LocalStandardsSeries] = "irs-local-standards",
        [Ch13MultipliersSeries] = "ch13-admin-multipliers",
    };

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<UstRelease>>> Shipped = new(() => LoadRegistry(RegistryTree.Shipped));

    /// <summary>The committed registry.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<UstRelease>> Registry => Shipped.Value;

    /// <summary>Loads and validates every UST release under a registry root; every series must exist.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<UstRelease>> LoadRegistry(RegistryTree root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var series = new SortedDictionary<string, IReadOnlyList<UstRelease>>(StringComparer.Ordinal);
        foreach (var name in root.ChildDirectories("ust"))
        {
            var seriesId = $"ust/{name}";
            if (!SeriesPayloads.ContainsKey(seriesId))
            {
                throw Fail(seriesId, "unknown UST series");
            }

            var loaded = root.ChildDirectories($"ust/{name}")
                .Select(release => LoadRelease(root, $"ust/{name}/{release}", release, seriesId))
                .ToList();
            if (loaded.Count == 0)
            {
                throw Fail(seriesId, "series has no releases");
            }

            series[seriesId] = Releases.Ordered(loaded, seriesId);
        }

        var missing = SeriesPayloads.Keys.Where(id => !series.ContainsKey(id)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new MalformedReleaseException($"the UST registry is missing series: [{string.Join(", ", missing.Select(Exact.Repr))}]");
        }

        return series;
    }

    /// <summary>The series ids, sorted.</summary>
    public static IReadOnlyList<string> SeriesIds() => [.. Registry.Keys.Order(StringComparer.Ordinal)];

    /// <summary>A series' releases, oldest first.</summary>
    public static IReadOnlyList<UstRelease> ReleasesOf(string seriesId) =>
        Registry.TryGetValue(seriesId, out var found) ? found : throw new EngineKeyException($"unknown series {Exact.Repr(seriesId)}");

    /// <summary>The release with the greatest effective date on or before <paramref name="asOf"/>.</summary>
    public static UstRelease PickRelease(IReadOnlyList<UstRelease> candidates, DateOnly asOf) =>
        Releases.Pick(candidates, asOf, "refusing to compute on data that does not describe that date");

    /// <summary>The release in force on <paramref name="asOf"/>.</summary>
    public static UstRelease Resolve(string seriesId, DateOnly asOf) => PickRelease(ReleasesOf(seriesId), asOf);

    /// <summary>That exact release.</summary>
    public static UstRelease Get(string seriesId, string releaseId) =>
        ReleasesOf(seriesId).FirstOrDefault(release => release.ReleaseId == releaseId)
        ?? throw new EngineKeyException($"series {Exact.Repr(seriesId)} has no release {Exact.Repr(releaseId)}");

    /// <summary>Newest by (effective date, sequence).</summary>
    public static UstRelease Latest(string seriesId) => ReleasesOf(seriesId)[^1];

    /// <summary>The median table in force on <paramref name="asOf"/>, with its release.</summary>
    public static (UstRelease Release, MedianIncomeTable Table) MedianIncomeTable(DateOnly asOf) => Payload<MedianIncomeTable>(Resolve(MedianIncomeSeries, asOf));

    /// <summary>The National Standards in force on <paramref name="asOf"/>, with their release.</summary>
    public static (UstRelease Release, NationalStandards Table) NationalStandards(DateOnly asOf) => Payload<NationalStandards>(Resolve(NationalStandardsSeries, asOf));

    /// <summary>The Local Standards in force on <paramref name="asOf"/>, with their release.</summary>
    public static (UstRelease Release, LocalStandards Table) LocalStandards(DateOnly asOf) => Payload<LocalStandards>(Resolve(LocalStandardsSeries, asOf));

    /// <summary>The Chapter 13 multipliers in force on <paramref name="asOf"/>, with their release.</summary>
    public static (UstRelease Release, Ch13AdminMultipliers Table) Ch13AdminMultipliers(DateOnly asOf) => Payload<Ch13AdminMultipliers>(Resolve(Ch13MultipliersSeries, asOf));

    internal static decimal Dec(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Counties match by their lower-cased name.")]
    internal static string NormalizeCounty(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var stripped = name.Trim();
        if (stripped.EndsWith(" County", StringComparison.Ordinal))
        {
            stripped = stripped[..^" County".Length];
        }

        return stripped.Trim().ToLowerInvariant();
    }

    [GeneratedRegex(@"^[A-Z]{2}$")]
    private static partial Regex StatePattern();

    [GeneratedRegex(@"^\d{5}$")]
    private static partial Regex FipsPattern();

    private static (UstRelease, T) Payload<T>(UstRelease release)
        where T : UstPayload =>
        release.Payload is T payload ? (release, payload) : throw new InvalidOperationException($"{release.ReleaseId} does not carry {typeof(T).Name}");

    private static IReadOnlyList<string> MoneyRow(JsonNode? value, string where, string fieldName, int count)
    {
        if (value is not JsonArray list || list.Count != count)
        {
            throw Fail(where, $"{fieldName} must be a list of {count} money strings");
        }

        return [.. list.Select((item, index) => Money(item, where, $"{fieldName}[{index}]"))];
    }

    private static VehicleCosts Vehicle(JsonNode? value, string where, string fieldName)
    {
        if (value is not JsonObject raw)
        {
            throw Fail(where, $"{fieldName} must be an object");
        }

        return new VehicleCosts(Money(raw["one_car"], where, $"{fieldName}.one_car"), Money(raw["two_cars"], where, $"{fieldName}.two_cars"));
    }

    private static MedianIncomeTable MediansPayload(JsonObject payload, string where)
    {
        if (payload["annual_medians"] is not JsonObject raw || raw.Count == 0)
        {
            throw Fail(where, "annual_medians missing or empty");
        }

        var medians = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (state, row) in raw)
        {
            if (!StatePattern().IsMatch(state))
            {
                throw Fail(where, $"annual_medians key {Exact.Repr(state)} is not a state code");
            }

            medians[state] = MoneyRow(row, where, $"annual_medians.{state}", 4);
        }

        return new MedianIncomeTable(medians, Money(payload["excess_person_annual_addition"], where, "excess_person_annual_addition"));
    }

    private static NationalStandards NationalPayload(JsonObject payload, string where)
    {
        string[] sizes = ["1", "2", "3", "4"];
        if (payload["monthly_allowances"] is not JsonObject rawAllowances
            || rawAllowances.Count != 4
            || !sizes.All(rawAllowances.ContainsKey))
        {
            throw Fail(where, "monthly_allowances must carry sizes 1-4 exactly");
        }

        var allowances = sizes.Select(size => Money(rawAllowances[size], where, $"monthly_allowances.{size}")).ToList();
        if (payload["components"] is not JsonArray rawComponents || rawComponents.Count == 0)
        {
            throw Fail(where, "components missing or empty");
        }

        var components = new List<NationalStandardComponent>();
        foreach (var node in rawComponents)
        {
            if (node is not JsonObject raw)
            {
                throw Fail(where, "component is not an object");
            }

            var item = StrField(raw, "item", where);
            components.Add(new NationalStandardComponent(item, MoneyRow(raw["monthly"], where, $"component {item}", 4)));
        }

        if (payload["oop_healthcare"] is not JsonObject oop)
        {
            throw Fail(where, "oop_healthcare missing");
        }

        var foodAndClothing = MoneyRow(payload["food_and_clothing"], where, "food_and_clothing", 4);
        var fivePercent = MoneyRow(payload["additional_food_clothing_cap"], where, "additional_food_clothing_cap", 4);
        return new NationalStandards(
            MonthlyAllowances: allowances,
            EachAdditionalPerson: Money(payload["each_additional_person"], where, "each_additional_person"),
            Components: components,
            FoodAndClothing: foodAndClothing,
            FoodAndClothingEachAdditionalPerson: Money(payload["food_and_clothing_each_additional_person"], where, "food_and_clothing_each_additional_person"),
            AdditionalFoodClothingCap: fivePercent,
            AdditionalFoodClothingCapEachAdditionalPerson: Money(payload["additional_food_clothing_cap_each_additional_person"], where, "additional_food_clothing_cap_each_additional_person"),
            OopHealthcareUnder65: Money(oop["under_65"], where, "oop_healthcare.under_65"),
            OopHealthcare65AndOlder: Money(oop["65_and_older"], where, "oop_healthcare.65_and_older"));
    }

    private static Ch13AdminMultipliers Ch13Payload(JsonObject payload, string where)
    {
        if (payload["multipliers"] is not JsonObject raw || raw.Count == 0)
        {
            throw Fail(where, "multipliers missing or empty");
        }

        var multipliers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (district, value) in raw)
        {
            if (district.Trim().Length == 0)
            {
                throw Fail(where, "multiplier district name missing");
            }

            var text = Core.Json.AsString(value) ?? throw Fail(where, $"{district}: multiplier must be a string");
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var fraction))
            {
                throw Fail(where, $"{district}: multiplier {Exact.Repr(text)}: [<class 'decimal.ConversionSyntax'>]");
            }

            if (fraction < 0 || fraction >= 1)
            {
                throw Fail(where, $"{district}: multiplier {Exact.Repr(text)} is not a fraction");
            }

            multipliers[district] = text;
        }

        return new Ch13AdminMultipliers(multipliers);
    }

    private static LocalStandards LocalPayload(JsonObject payload, string where)
    {
        if (payload["states"] is not JsonArray rawStates
            || rawStates.Count == 0
            || !rawStates.All(state => Core.Json.AsString(state) is { } text && StatePattern().IsMatch(text)))
        {
            throw Fail(where, "states must be a non-empty list of state codes");
        }

        var states = rawStates.Select(state => Core.Json.AsString(state)!).ToList();
        var stateSet = states.ToHashSet(StringComparer.Ordinal);
        if (payload["housing_utilities"] is not JsonObject rawHousing || !rawHousing.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(stateSet))
        {
            throw Fail(where, "housing_utilities must carry exactly the listed states");
        }

        var housing = new Dictionary<string, IReadOnlyList<CountyHousing>>(StringComparer.Ordinal);
        foreach (var (state, rawRows) in rawHousing)
        {
            if (rawRows is not JsonArray rowList || rowList.Count == 0)
            {
                throw Fail(where, $"housing_utilities.{state} missing or empty");
            }

            var rows = new List<CountyHousing>();
            foreach (var node in rowList)
            {
                if (node is not JsonObject raw)
                {
                    throw Fail(where, $"housing_utilities.{state} row is not an object");
                }

                var county = StrField(raw, "county", where);
                var fips = StrField(raw, "fips", where);
                if (!FipsPattern().IsMatch(fips))
                {
                    throw Fail(where, $"{state} {county}: fips {Exact.Repr(fips)} is not 5 digits");
                }

                var nonMortgage = MoneyRow(raw["non_mortgage"], where, $"{state} {county} non_mortgage", 5);
                var mortgageRent = MoneyRow(raw["mortgage_rent"], where, $"{state} {county} mortgage_rent", 5);
                rows.Add(new CountyHousing(fips, county, nonMortgage, mortgageRent));
            }

            var counties = rows.Select(row => NormalizeCounty(row.County)).ToList();
            if (counties.Distinct(StringComparer.Ordinal).Count() != counties.Count)
            {
                throw Fail(where, $"housing_utilities.{state} has duplicate counties");
            }

            housing[state] = rows;
        }

        if (payload["transportation"] is not JsonObject rawTransport)
        {
            throw Fail(where, "transportation missing");
        }

        if (rawTransport["operating_costs_region"] is not JsonObject rawRegionMap || !rawRegionMap.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(stateSet))
        {
            throw Fail(where, "operating_costs_region must carry exactly the listed states");
        }

        var regionMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (state, _) in rawRegionMap)
        {
            regionMap[state] = StrField(rawRegionMap, state, where);
        }

        if (rawTransport["operating_costs"] is not JsonObject rawRegions || !regionMap.Values.All(rawRegions.ContainsKey))
        {
            throw Fail(where, "operating_costs missing a region the map names");
        }

        var regions = new Dictionary<string, VehicleCosts>(StringComparer.Ordinal);
        foreach (var (name, rawCosts) in rawRegions)
        {
            regions[name] = Vehicle(rawCosts, where, $"operating_costs.{name}");
        }

        if (rawTransport["operating_costs_msa"] is not JsonObject rawMsas)
        {
            throw Fail(where, "operating_costs_msa must be an object");
        }

        var msas = new List<MsaOperatingCosts>();
        foreach (var (name, node) in rawMsas)
        {
            if (node is not JsonObject rawMsa)
            {
                throw Fail(where, $"operating_costs_msa.{name} is not an object");
            }

            var state = StrField(rawMsa, "state", where);
            if (!stateSet.Contains(state))
            {
                throw Fail(where, $"MSA {name} names state {Exact.Repr(state)}, not in states");
            }

            if (rawMsa["counties"] is not JsonArray rawCounties || !rawCounties.All(county => Core.Json.AsString(county) is { Length: > 0 }))
            {
                throw Fail(where, $"MSA {name}: counties must be a list of names");
            }

            msas.Add(new MsaOperatingCosts(
                Money(rawMsa["one_car"], where, $"MSA {name} one_car"),
                Money(rawMsa["two_cars"], where, $"MSA {name} two_cars"),
                state,
                [.. rawCounties.Select(county => Core.Json.AsString(county)!)]));
        }

        var transportation = new Transportation(
            PublicTransportationNational: Money(rawTransport["public_transportation_national"], where, "public_transportation_national"),
            OwnershipCosts: Vehicle(rawTransport["ownership_costs"], where, "ownership_costs"),
            OperatingCostsRegion: regionMap,
            OperatingCosts: regions,
            OperatingCostsMsa: msas);
        return new LocalStandards(states, housing, transportation);
    }

    private static UstRelease LoadRelease(RegistryTree root, string directory, string name, string seriesId)
    {
        var where = $"{seriesId}/{name}";
        var (effective, sequence) = ParseDirectoryName(name, where);
        var manifest = LoadJson(root, $"{directory}/manifest.json", where);
        var source = ManifestSource(manifest, seriesId, effective, sequence, where);
        var sha256 = Sha256(source, where);
        var manifestNotes = ManifestNotes(manifest, where);

        var payloadRaw = LoadJson(root, $"{directory}/dataset.json", where);
        if (Core.Json.AsString(payloadRaw["kind"]) != SeriesPayloads[seriesId])
        {
            throw Fail(where, $"dataset kind {Exact.Repr(payloadRaw["kind"])}");
        }

        var verification = ParseVerification(payloadRaw["verification"], where);
        if (verification == Verification.Unverified)
        {
            throw Fail(where, "a means-testing dataset may never be unverified");
        }

        UstPayload payload = seriesId switch
        {
            MedianIncomeSeries => MediansPayload(payloadRaw, where),
            NationalStandardsSeries => NationalPayload(payloadRaw, where),
            Ch13MultipliersSeries => Ch13Payload(payloadRaw, where),
            _ => LocalPayload(payloadRaw, where),
        };

        return new UstRelease(
            SeriesId: seriesId,
            EffectiveDate: effective,
            Sequence: sequence,
            SourceUrl: StrField(source, "url", where),
            SourcePublished: DateOrNone(source["published"], where, "source.published"),
            SourceSha256: sha256,
            Notes: manifestNotes,
            Verification: verification,
            Sources: Sources(payloadRaw["sources"], where),
            Payload: payload);
    }
}
