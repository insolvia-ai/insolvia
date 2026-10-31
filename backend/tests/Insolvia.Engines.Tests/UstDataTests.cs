using System.Text.Json.Nodes;
using Insolvia.Engines.Regulatory;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_ust_data.py: the committed
// registry swept and pinned to the UST's own tables, the resolution
// semantics, and the loader against malformed releases.
public sealed class UstDataTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 4);

    // --- the committed registry loads, whole ---

    [Fact]
    public void The_registry_holds_the_four_series() =>
        Assert.Equal([UstData.MedianIncomeSeries, UstData.Ch13MultipliersSeries, UstData.LocalStandardsSeries, UstData.NationalStandardsSeries], UstData.SeriesIds());

    [Fact]
    public void Every_committed_release_is_verified_and_sourced()
    {
        foreach (var seriesId in UstData.SeriesIds())
        {
            foreach (var release in UstData.ReleasesOf(seriesId))
            {
                Assert.NotEqual(Verification.Unverified, release.Verification);
                Assert.NotEmpty(release.Sources);
                Assert.False(string.IsNullOrEmpty(release.SourceSha256));
            }
        }
    }

    // --- the median table (B122A-1 line 13) ---

    [Fact]
    public void The_launch_state_medians_match_the_ust_table()
    {
        var (_, table) = UstData.MedianIncomeTable(AsOf);
        Assert.Equal(["69876.00", "86523.00", "97540.00", "114761.00"], table.AnnualMedians["FL"]);
        Assert.Equal(["68478.00", "84965.00", "101479.00", "123481.00"], table.AnnualMedians["GA"]);
        Assert.Equal(["66837.00", "86714.00", "99273.00", "117962.00"], table.AnnualMedians["TX"]);
    }

    [Fact]
    public void Households_above_four_add_the_excess_person_figure()
    {
        var (_, table) = UstData.MedianIncomeTable(AsOf);
        Assert.Equal(table.AnnualMedian("FL", 4) + (2 * Exact.Dec(table.ExcessPersonAnnualAddition)), table.AnnualMedian("FL", 6));
    }

    [Fact]
    public void The_excess_addition_is_twelve_times_the_statutory_monthly_addon()
    {
        var (_, table) = UstData.MedianIncomeTable(AsOf);
        var addon = DollarAmounts.Latest().Amount("median-addon-per-person-safe-harbor");
        Assert.Equal(12 * addon.Value, Exact.Dec(table.ExcessPersonAnnualAddition));
    }

    [Fact]
    public void The_median_table_covers_every_state_and_territory()
    {
        var (_, table) = UstData.MedianIncomeTable(AsOf);
        Assert.Equal(55, table.AnnualMedians.Count);
        Assert.Subset(table.AnnualMedians.Keys.ToHashSet(), new HashSet<string> { "DC", "GU", "MP", "PR", "VI" });
    }

    [Fact]
    public void An_unknown_jurisdiction_and_a_bad_size_are_refused()
    {
        var (_, table) = UstData.MedianIncomeTable(AsOf);
        Assert.Contains("no row", Assert.Throws<EngineKeyException>(() => table.AnnualMedian("ZZ", 2)).Message, StringComparison.Ordinal);
        Assert.Contains("not positive", Assert.Throws<EngineValueException>(() => table.AnnualMedian("FL", 0)).Message, StringComparison.Ordinal);
    }

    // --- the National Standards (B122A-2 lines 6-7) ---

    [Fact]
    public void The_national_allowances_match_the_ust_table()
    {
        var (_, standards) = UstData.NationalStandards(AsOf);
        Assert.Equal(["867.00", "1558.00", "1857.00", "2176.00"], standards.MonthlyAllowances);
        Assert.Equal("397.00", standards.EachAdditionalPerson);
        Assert.Equal(2176.00m + (2 * 397.00m), standards.Allowance(6));
    }

    [Fact]
    public void The_component_lines_sum_to_the_total()
    {
        var (_, standards) = UstData.NationalStandards(AsOf);
        for (var size = 0; size < 4; size++)
        {
            Assert.Equal(Exact.Dec(standards.MonthlyAllowances[size]), standards.Components.Sum(component => Exact.Dec(component.Monthly[size])));
        }
    }

    [Fact]
    public void The_additional_food_clothing_cap_is_five_percent_rounded()
    {
        var (_, standards) = UstData.NationalStandards(AsOf);
        for (var size = 0; size < 4; size++)
        {
            var expected = Exact.Quantize(0.05m * Exact.Dec(standards.FoodAndClothing[size]), Rounding.HalfUp, places: 0);
            Assert.Equal(expected, Exact.Dec(standards.AdditionalFoodClothingCap[size]));
        }

        Assert.Equal(
            Exact.Dec(standards.AdditionalFoodClothingCap[3]) + (2 * Exact.Dec(standards.AdditionalFoodClothingCapEachAdditionalPerson)),
            standards.AdditionalFoodClothingCapFor(6));
    }

    [Fact]
    public void The_oop_healthcare_allowance_is_per_person_by_age_band()
    {
        var (_, standards) = UstData.NationalStandards(AsOf);
        Assert.Equal("90.00", standards.OopHealthcareUnder65);
        Assert.Equal("163.00", standards.OopHealthcare65AndOlder);
        Assert.Equal(343.00m, standards.OopHealthcare(under65: 2, over65: 1));
    }

    // --- the Local Standards (B122A-2 lines 8-14) ---

    [Fact]
    public void The_housing_tables_cover_every_launch_state_county()
    {
        var (_, standards) = UstData.LocalStandards(AsOf);
        Assert.Equal(["FL", "GA", "TX"], standards.States);
        Assert.Equal(67, standards.Housing["FL"].Count);
        Assert.Equal(159, standards.Housing["GA"].Count);
        Assert.Equal(254, standards.Housing["TX"].Count);
    }

    [Fact]
    public void A_county_row_matches_the_ust_spreadsheet()
    {
        var (_, standards) = UstData.LocalStandards(AsOf);
        var alachua = standards.HousingFor("FL", "Alachua County");
        Assert.Equal("12001", alachua.Fips);
        Assert.Equal(727.00m, alachua.NonMortgageFor(3));
        Assert.Equal(1517.00m, alachua.MortgageRentFor(3));
        Assert.Equal(alachua.NonMortgageFor(5), alachua.NonMortgageFor(9));
    }

    [Fact]
    public void County_lookup_tolerates_the_county_suffix()
    {
        var (_, standards) = UstData.LocalStandards(AsOf);
        Assert.Equal(standards.HousingFor("fl", "Alachua County"), standards.HousingFor("FL", "Alachua"));
        Assert.Contains("no housing standard", Assert.Throws<EngineKeyException>(() => standards.HousingFor("FL", "Nowhere")).Message, StringComparison.Ordinal);
        Assert.Contains("launch states", Assert.Throws<EngineKeyException>(() => standards.HousingFor("CA", "Alameda")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Operating_costs_use_the_msa_override_or_the_region()
    {
        var transportation = UstData.LocalStandards(AsOf).Table.Transportation;
        Assert.Equal(423.00m, transportation.OperatingCostsFor("FL", "Miami-Dade").ForVehicles(1));
        Assert.Equal(291.00m, transportation.OperatingCostsFor("FL", "Alachua County").ForVehicles(1));
        Assert.Equal(638.00m, transportation.OperatingCostsFor("GA", "Fulton County").ForVehicles(2));
    }

    [Fact]
    public void The_national_transportation_figures_match_the_ust_table()
    {
        var transportation = UstData.LocalStandards(AsOf).Table.Transportation;
        Assert.Equal("220.00", transportation.PublicTransportationNational);
        Assert.Equal(703.00m, transportation.OwnershipCosts.ForVehicles(1));
        Assert.Equal(1406.00m, transportation.OwnershipCosts.ForVehicles(2));
        Assert.Contains("no column", Assert.Throws<EngineValueException>(() => transportation.OwnershipCosts.ForVehicles(3)).Message, StringComparison.Ordinal);
    }

    // --- the Chapter 13 multipliers (B122A-2 line 36) ---

    [Fact]
    public void The_launch_district_multipliers_match_the_ust_table()
    {
        var (_, table) = UstData.Ch13AdminMultipliers(AsOf);
        Assert.Equal(0.1m, table.MultiplierFor("Middle Florida"));
        Assert.Equal(0.077m, table.MultiplierFor("Northern Georgia"));
        Assert.Equal(0.098m, table.MultiplierFor("Southern Texas"));
    }

    [Fact]
    public void District_lookup_accepts_the_courts_spelling()
    {
        var (_, table) = UstData.Ch13AdminMultipliers(AsOf);
        Assert.Equal(table.MultiplierFor("Middle Florida"), table.MultiplierFor("Middle District of Florida"));
        Assert.Equal(0.091m, table.MultiplierFor("District of Columbia"));
        Assert.Contains("no district", Assert.Throws<EngineKeyException>(() => table.MultiplierFor("Outer District of Nowhere")).Message, StringComparison.Ordinal);
    }

    // --- resolution semantics ---

    [Fact]
    public void Resolution_before_a_series_begins_refuses() =>
        Assert.Contains("refusing", Assert.Throws<EngineLookupException>(() => UstData.Resolve(UstData.MedianIncomeSeries, new DateOnly(2026, 3, 31))).Message, StringComparison.Ordinal);

    [Fact]
    public void Resolve_get_and_latest_agree()
    {
        foreach (var seriesId in UstData.SeriesIds())
        {
            var release = UstData.Resolve(seriesId, AsOf);
            Assert.Same(release, UstData.Get(seriesId, release.ReleaseId));
            Assert.Same(release, UstData.Latest(seriesId));
        }
    }

    [Fact]
    public void An_unknown_series_is_a_key_error() =>
        Assert.Contains("unknown series", Assert.Throws<EngineKeyException>(() => UstData.ReleasesOf("ust/nowhere")).Message, StringComparison.Ordinal);

    [Fact]
    public void The_typed_accessors_carry_their_payload_types()
    {
        Assert.IsType<MedianIncomeTable>(UstData.MedianIncomeTable(AsOf).Table);
        Assert.IsType<NationalStandards>(UstData.NationalStandards(AsOf).Table);
        Assert.IsType<LocalStandards>(UstData.LocalStandards(AsOf).Table);
        Assert.IsType<Ch13AdminMultipliers>(UstData.Ch13AdminMultipliers(AsOf).Table);
    }

    [Fact]
    public void A_correction_wins_future_resolution_by_sequence()
    {
        var original = Stub(new DateOnly(2026, 4, 1), 1);
        var correction = Stub(new DateOnly(2026, 4, 1), 2);
        var picked = UstData.PickRelease([original, correction], new DateOnly(2026, 6, 1));
        Assert.Same(correction, picked);
        Assert.Equal("ust/census-median-family-income@2026-04-01+2", picked.ReleaseId);
    }

    // --- the loader rejects malformed releases ---

    [Fact]
    public void The_loader_accepts_a_well_formed_registry()
    {
        var loaded = UstData.LoadRegistry(Registry());
        Assert.Equal(UstData.SeriesIds().ToHashSet(), loaded.Keys.ToHashSet());
        Assert.IsType<MedianIncomeTable>(loaded[UstData.MedianIncomeSeries][0].Payload);
    }

    [Fact]
    public void A_missing_series_is_a_packaging_failure() =>
        Assert.Contains("missing series", Assert.Throws<MalformedReleaseException>(() => UstData.LoadRegistry(Registry(omit: "irs-national-standards"))).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("census-median-family-income", """{"kind": "wrong"}""", "dataset kind")]
    [InlineData("census-median-family-income", """{"verification": "unverified"}""", "unverified")]
    [InlineData("census-median-family-income", """{"annual_medians": {"Florida": ["1.00", "2.00", "3.00", "4.00"]}}""", "not a state code")]
    [InlineData("census-median-family-income", """{"annual_medians": {"FL": ["1.00", "2.00", "3.00"]}}""", "list of 4")]
    [InlineData("irs-national-standards", """{"monthly_allowances": {"1": "10.00"}}""", "sizes 1-4")]
    [InlineData("irs-local-standards", """{"housing_utilities": {"GA": []}}""", "exactly the listed states")]
    [InlineData("irs-local-standards", """{"transportation": {"public_transportation_national": "220.00", "ownership_costs": {"one_car": "703.00", "two_cars": "1406.00"}, "operating_costs_region": {"FL": "west"}, "operating_costs": {"south": {"one_car": "291.00", "two_cars": "582.00"}}, "operating_costs_msa": {}}}""", "missing a region")]
    public void The_loader_rejects_a_malformed_payload(string series, string overrides, string problem) =>
        Assert.Contains(problem, Assert.Throws<MalformedReleaseException>(() => UstData.LoadRegistry(Registry(series, overrides))).Message, StringComparison.Ordinal);

    [Fact]
    public void The_loaded_registry_is_what_the_module_serves() =>
        Assert.Equal(UstData.SeriesIds().ToHashSet(), UstData.Registry.Keys.ToHashSet());

    private static UstRelease Stub(DateOnly effective, int sequence) => new(
        UstData.MedianIncomeSeries,
        effective,
        sequence,
        "https://example.gov/",
        null,
        null,
        "stub",
        Verification.Primary,
        [],
        new MedianIncomeTable(new Dictionary<string, IReadOnlyList<string>> { ["FL"] = ["1.00", "2.00", "3.00", "4.00"] }, "5.00"));

    private const string Sources = """[{"title": "stub", "url": "https://example.gov/", "accessed": "2026-09-04"}]""";

    private static readonly Dictionary<string, string> Payloads = new(StringComparer.Ordinal)
    {
        ["census-median-family-income"] = """{"kind": "census-median-family-income", "verification": "primary", "sources": SOURCES, "annual_medians": {"FL": ["100.00", "200.00", "300.00", "400.00"]}, "excess_person_annual_addition": "50.00"}""",
        ["irs-national-standards"] = """{"kind": "irs-national-standards", "verification": "primary", "sources": SOURCES, "monthly_allowances": {"1": "10.00", "2": "20.00", "3": "30.00", "4": "40.00"}, "each_additional_person": "5.00", "components": [{"item": "Food", "monthly": ["10.00", "20.00", "30.00", "40.00"]}], "food_and_clothing": ["8.00", "16.00", "24.00", "32.00"], "food_and_clothing_each_additional_person": "4.00", "additional_food_clothing_cap": ["1.00", "2.00", "3.00", "4.00"], "additional_food_clothing_cap_each_additional_person": "1.00", "oop_healthcare": {"under_65": "90.00", "65_and_older": "163.00"}}""",
        ["irs-local-standards"] = """{"kind": "irs-local-standards", "verification": "primary", "sources": SOURCES, "states": ["FL"], "housing_utilities": {"FL": [{"fips": "12001", "county": "Alachua County", "non_mortgage": ["1.00", "2.00", "3.00", "4.00", "5.00"], "mortgage_rent": ["1.00", "2.00", "3.00", "4.00", "5.00"]}]}, "transportation": {"public_transportation_national": "220.00", "ownership_costs": {"one_car": "703.00", "two_cars": "1406.00"}, "operating_costs_region": {"FL": "south"}, "operating_costs": {"south": {"one_car": "291.00", "two_cars": "582.00"}}, "operating_costs_msa": {}}}""",
        ["ch13-admin-multipliers"] = """{"kind": "ch13-admin-multipliers", "verification": "primary", "sources": SOURCES, "multipliers": {"Middle Florida": "0.1"}}""",
    };

    private static RegistryTree Registry(string? series = null, string? overrides = null, string? omit = null)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, payload) in Payloads)
        {
            if (name == omit)
            {
                continue;
            }

            var manifest = new JsonObject
            {
                ["series_id"] = $"ust/{name}",
                ["effective_date"] = "2026-04-01",
                ["sequence"] = 1,
                ["source"] = new JsonObject { ["url"] = "https://example.gov/", ["published"] = null, ["sha256"] = string.Concat(Enumerable.Repeat("ab", 32)) },
                ["notes"] = "a well-formed stub release",
            };
            files[$"ust/{name}/2026-04-01/manifest.json"] = manifest.ToJsonString();
            files[$"ust/{name}/2026-04-01/dataset.json"] = Trees.Merge(payload.Replace("SOURCES", Sources, StringComparison.Ordinal), name == series ? overrides : null).ToJsonString();
        }

        return new RegistryTree(files);
    }
}
