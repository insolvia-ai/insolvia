using System.Text.Json.Nodes;
using Insolvia.Core.Courts;

namespace Insolvia.Core.Tests;

/// <summary>
/// The court registry (issue #360), translated from test_courts.py: the real
/// loader over the real (embedded) registry — the CI validation ADR 0014 asks
/// of a series' first consumer.
/// </summary>
public sealed class CourtRegistryTests
{
    private static readonly HashSet<string> LaunchDistricts = ["flnb", "flmb", "flsb", "txnb", "txsb", "txeb", "txwb", "ganb", "gamb", "gasb"];

    private static readonly HashSet<string> B101Names =
    [
        "Northern District of Florida",
        "Middle District of Florida",
        "Southern District of Florida",
        "Northern District of Texas",
        "Southern District of Texas",
        "Eastern District of Texas",
        "Western District of Texas",
        "Northern District of Georgia",
        "Middle District of Georgia",
        "Southern District of Georgia",
    ];

    private static CourtRegistry Registry => CourtRegistry.Shipped;

    private static CourtsRelease Release => Registry.Latest;

    [Fact]
    public void The_registry_holds_exactly_the_launch_districts()
    {
        Assert.Equal(LaunchDistricts, Release.Districts.Select(d => d.Code).ToHashSet());
        Assert.Equal(B101Names, Release.Districts.Select(d => d.Name).ToHashSet());
    }

    [Fact]
    public void Every_district_has_a_pacer_court_id()
    {
        foreach (var district in Release.Districts)
        {
            Assert.Equal(district.Code.ToUpperInvariant() + "K", district.CourtId);
        }
    }

    [Fact]
    public void Every_county_of_a_district_is_in_exactly_one_division()
    {
        foreach (var district in Release.Districts)
        {
            var seen = new HashSet<string>();
            foreach (var county in district.Divisions.SelectMany(division => division.Counties))
            {
                Assert.True(seen.Add(county.Fips), $"{district.Code} {county}");
            }

            Assert.NotEmpty(seen);
        }
    }

    [Theory]
    [InlineData("flnb", 23)]
    [InlineData("flmb", 35)]
    [InlineData("flsb", 9)]
    [InlineData("txnb", 100)]
    [InlineData("txsb", 43)]
    [InlineData("txeb", 43)]
    [InlineData("txwb", 68)]
    [InlineData("ganb", 46)]
    [InlineData("gamb", 70)]
    [InlineData("gasb", 43)]
    public void County_counts_match_the_venue_statutes(string code, int expected)
    {
        var district = Release.District(code);
        Assert.NotNull(district);
        Assert.Equal(expected, district.Divisions.Sum(division => division.Counties.Count));
    }

    [Fact]
    public void A_county_resolves_to_its_division()
    {
        var flmb = Release.District("flmb")!;
        Assert.Equal("tampa", flmb.DivisionForCounty("12057")?.Code);
        Assert.Null(flmb.DivisionForCounty("48201")); // Harris, TX
    }

    [Fact]
    public void Every_verified_fact_cites_a_source_read_on_a_date()
    {
        foreach (var district in Release.Districts)
        {
            var ids = district.Sources.Select(source => source.Id).ToHashSet();
            void Check(IFact fact, string where)
            {
                if (fact.Verified)
                {
                    Assert.True(fact.Source is not null && ids.Contains(fact.Source), where);
                    Assert.True(fact.VerifiedAt is not null, where);
                }
            }

            foreach (var division in district.Divisions)
            {
                Check(division.OfficeCode, $"{district.Code} {division.Code}");
                Check(division.Courthouse, $"{district.Code} {division.Code}");
                Assert.Contains(division.CountiesSource, ids);
            }

            foreach (var fact in district.Cmecf.Facts().Concat(district.Pdf.Facts()).Concat(district.Matrix.Facts()))
            {
                Check(fact, district.Code);
            }

            Check(district.Opening.SignatureInstrument, district.Code);
            Check(district.Opening.SsnStatement, district.Code);
            Check(district.Opening.FeeRule, district.Code);
            Check(district.Registration.TrainingRequired, district.Code);
            Check(district.Chapter13Plan, district.Code);
        }
    }

    [Fact]
    public void Every_district_states_its_chapter_13_plan_form()
    {
        var files = CourtRegistry.EmbeddedFiles();
        var release = Release.ReleaseId.Split('@')[1];
        foreach (var (path, text) in files.Where(pair => pair.Key.StartsWith($"courts/us-bankruptcy/{release}/districts/", StringComparison.Ordinal)))
        {
            Assert.True(JsonNode.Parse(text)!.AsObject().ContainsKey("chapter_13_plan"), path);
        }

        foreach (var district in Release.Districts)
        {
            Assert.True(district.Chapter13Plan.Verified || district.Chapter13Plan.Value is null, district.Code);
        }
    }

    [Fact]
    public void Flsb_takes_its_local_plan_form()
    {
        var fact = Registry.District("flsb")!.Chapter13Plan;
        Assert.True(fact.Verified);
        Assert.Equal(Chapter13PlanFormKind.Local, fact.Value?.Form);
    }

    [Theory]
    [InlineData("2026-09-24")]
    [InlineData("2026-09-26")]
    public void A_release_before_the_fact_loads_it_as_unknown(string releaseId)
    {
        foreach (var district in Registry.Get($"courts/us-bankruptcy@{releaseId}").Districts)
        {
            Assert.False(district.Chapter13Plan.Verified);
            Assert.Null(district.Chapter13Plan.Value);
        }
    }

    [Fact]
    public void The_plan_form_release_adds_only_the_plan_form_fact()
    {
        var before = Registry.Get("courts/us-bankruptcy@2026-09-26");
        var after = Registry.Get("courts/us-bankruptcy@2026-09-26+2");
        foreach (var (old, @new) in before.Districts.Zip(after.Districts))
        {
            Assert.Equal(old, @new with { Chapter13Plan = old.Chapter13Plan });
        }
    }

    [Fact]
    public void Case_upload_is_unverified_everywhere_until_a_training_session()
    {
        foreach (var district in Release.Districts)
        {
            Assert.Equal(CaseUploadStatus.Unverified, district.CaseUpload.Status);
            Assert.Null(district.CaseUpload.VerifiedAt);
        }
    }

    [Fact]
    public void No_pdf_a_requirement_is_recorded_as_true()
    {
        Assert.All(Release.Districts, district => Assert.NotEqual(true, district.Pdf.PdfARequired.Value));
    }

    [Fact]
    public void Resolution_follows_effective_dating()
    {
        Assert.Equal(Release, Registry.Resolve(Clock.Today()));
        Assert.Equal(Release, Registry.Get(Release.ReleaseId));
        Assert.Throws<KeyNotFoundException>(() => Registry.Resolve(new DateOnly(2000, 1, 1)));
        Assert.Throws<KeyNotFoundException>(() => Registry.Get("courts/us-bankruptcy@1999-01-01"));
    }

    [Fact]
    public void The_second_release_changes_only_middle_georgias_matrix()
    {
        var first = Registry.Get("courts/us-bankruptcy@2026-09-24");
        var second = Registry.Get("courts/us-bankruptcy@2026-09-26");
        foreach (var (old, @new) in first.Districts.Zip(second.Districts))
        {
            if (old.Code != "gamb")
            {
                Assert.Equal(old, @new);
                continue;
            }

            Assert.NotEqual(old.Matrix, @new.Matrix);
            Assert.Equal(old, @new with { Matrix = old.Matrix, Sources = old.Sources, Notes = old.Notes });
        }

        var rules = second.District("gamb")!.Matrix;
        Assert.True(rules.BlankLinesBetween.Verified);
        Assert.Equal(2, rules.BlankLinesBetween.Value);
        Assert.True(rules.CommaAfterCity.Value);
        Assert.False(rules.CaseNumberHeaderWhenSeparate.Value);
        Assert.False(rules.CertificationRequired.Verified);
    }

    [Fact]
    public void The_lookup_pair_refuses_a_division_of_another_court()
    {
        Assert.NotNull(Registry.Division("flmb", "tampa"));
        Assert.Null(Registry.Division("flmb", "miami"));
        Assert.Null(Registry.Division("nyeb", "brooklyn"));
        Assert.NotNull(Registry.District("flsb"));
        Assert.True(Registry.HasDivision("flmb", "tampa"));
        Assert.False(Registry.HasDistrict("nyeb"));
    }

    [Fact]
    public void Every_source_is_a_court_government_or_statute_host()
    {
        string[] allowed = [".uscourts.gov", ".census.gov", "www.law.cornell.edu"];
        var seen = 0;
        foreach (var (path, text) in CourtRegistry.EmbeddedFiles())
        {
            if (JsonNode.Parse(text)!["sources"] is not JsonArray sources)
            {
                continue;
            }

            foreach (var source in sources)
            {
                var host = new Uri((string)source!["url"]!).Host;
                Assert.True(allowed.Any(a => host == a || host.EndsWith(a, StringComparison.Ordinal)), $"{path} {host}");
                seen++;
            }
        }

        Assert.True(seen > 0);
    }

    [Fact]
    public void A_malformed_record_fails_the_load()
    {
        var real = District("2026-09-24", "flsb");
        real["pdf"]!["max_bytes"]!["source"] = "S999";
        var files = new Dictionary<string, string>
        {
            ["courts/us-bankruptcy/2026-01-01/manifest.json"] = """{"series_id": "courts/us-bankruptcy", "effective_date": "2026-01-01", "sequence": 1, "source": {"url": "https://example.test"}, "notes": "a test release"}""",
            ["courts/us-bankruptcy/2026-01-01/districts/flsb.json"] = real.ToJsonString(),
        };

        var error = Assert.Throws<InvalidDataException>(() => CourtRegistryLoader.Load(files));
        Assert.Contains("S999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_plan_form_without_a_title_fails_the_load()
    {
        var real = District("2026-09-26+2", "flsb");
        real["chapter_13_plan"]!["value"]!["title"] = null;
        var error = Assert.Throws<InvalidDataException>(() => CourtRegistryLoader.Load(OneDistrict("2026-09-26+2", "flsb", real)));
        Assert.Contains("needs a title", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_district_states_its_docket_order_and_file_names()
    {
        var release = Release.ReleaseId.Split('@')[1];
        foreach (var (path, text) in CourtRegistry.EmbeddedFiles().Where(pair => pair.Key.StartsWith($"courts/us-bankruptcy/{release}/districts/", StringComparison.Ordinal)))
        {
            var opening = JsonNode.Parse(text)!["opening"]!.AsObject();
            Assert.True(opening.ContainsKey("docket_order"), path);
            Assert.True(opening.ContainsKey("file_names"), path);
        }

        foreach (var district in Release.Districts)
        {
            foreach (IFact fact in new IFact[] { district.Opening.DocketOrder, district.Opening.FileNames })
            {
                Assert.True(fact.Verified || (fact is Fact<ValueCollection<string>> { Value: null } or Fact<ValueDictionary<string, string>> { Value: null }), district.Code);
                Assert.NotEmpty(fact.Note);
            }
        }
    }

    [Theory]
    [InlineData("2026-09-24")]
    [InlineData("2026-09-26")]
    [InlineData("2026-09-26+2")]
    public void A_release_before_the_filing_set_facts_loads_them_as_unknown(string releaseId)
    {
        foreach (var district in Registry.Get($"courts/us-bankruptcy@{releaseId}").Districts)
        {
            Assert.Null(district.Opening.DocketOrder.Value);
            Assert.False(district.Opening.DocketOrder.Verified);
            Assert.Null(district.Opening.FileNames.Value);
            Assert.Contains("2026-10-06", district.Opening.FileNames.Note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_filing_set_release_adds_only_the_two_opening_facts()
    {
        var before = Registry.Get("courts/us-bankruptcy@2026-09-26+2");
        var after = Registry.Get("courts/us-bankruptcy@2026-10-06");
        foreach (var (old, @new) in before.Districts.Zip(after.Districts))
        {
            var opening = @new.Opening with { DocketOrder = old.Opening.DocketOrder, FileNames = old.Opening.FileNames };
            Assert.Equal(old, @new with { Opening = opening });
        }
    }

    [Fact]
    public void A_docket_order_with_an_unknown_document_key_fails_the_load()
    {
        var error = Assert.Throws<InvalidDataException>(() => CourtRegistryLoader.Load(FilingSetRelease(opening =>
            opening["docket_order"]!["value"] = new JsonArray("form/b101", "the petition"))));
        Assert.Contains("not a document key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_docket_order_naming_a_document_twice_fails_the_load()
    {
        var error = Assert.Throws<InvalidDataException>(() => CourtRegistryLoader.Load(FilingSetRelease(opening =>
            opening["docket_order"]!["value"] = new JsonArray("form/b101", "form/b101"))));
        Assert.Contains("appears twice", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../petition.pdf")]
    [InlineData("petition.exe")]
    [InlineData("petition.pdf\n")]
    public void A_file_name_that_is_not_a_plain_upload_name_fails_the_load(string name)
    {
        var error = Assert.Throws<InvalidDataException>(() => CourtRegistryLoader.Load(FilingSetRelease(opening =>
            opening["file_names"]!["value"] = new JsonObject { ["form/b101"] = name })));
        Assert.Contains("plain .pdf/.txt file name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_well_formed_docket_order_and_file_names_load()
    {
        var loaded = Assert.Single(CourtRegistryLoader.Load(FilingSetRelease(opening =>
        {
            opening["docket_order"]!["value"] = new JsonArray("form/b101", "creditor_matrix");
            opening["file_names"]!["value"] = new JsonObject { ["form/b101"] = "Petition.pdf", ["creditor_matrix"] = "Creditor.txt" };
        })));
        var txwb = loaded.District("txwb")!;
        Assert.Equal(["form/b101", "creditor_matrix"], txwb.Opening.DocketOrder.Value!);
        Assert.Equal("Petition.pdf", txwb.Opening.FileNames.Value!["form/b101"]);
        Assert.Equal("Creditor.txt", txwb.Opening.FileNames.Value!["creditor_matrix"]);
    }

    private static JsonObject District(string release, string code) =>
        JsonNode.Parse(CourtRegistry.EmbeddedFiles()[$"courts/us-bankruptcy/{release}/districts/{code}.json"])!.AsObject();

    private static Dictionary<string, string> OneDistrict(string release, string code, JsonObject record) => new()
    {
        [$"courts/us-bankruptcy/{release}/manifest.json"] = CourtRegistry.EmbeddedFiles()[$"courts/us-bankruptcy/{release}/manifest.json"],
        [$"courts/us-bankruptcy/{release}/districts/{code}.json"] = record.ToJsonString(),
    };

    private static Dictionary<string, string> FilingSetRelease(Action<JsonObject> mutate)
    {
        var real = District("2026-10-06", "txwb");
        mutate(real["opening"]!.AsObject());
        return OneDistrict("2026-10-06", "txwb", real);
    }
}
