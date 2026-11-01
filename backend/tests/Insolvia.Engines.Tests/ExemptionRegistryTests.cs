using System.Text.Json.Nodes;
using Insolvia.Engines.Regulatory;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_exemptions.py: the loader
// against malformed releases, and the committed registry swept with
// consistency checks.
public sealed class ExemptionRegistryTests
{
    private static readonly DateOnly September1 = new(2026, 9, 1);

    private static IEnumerable<ExemptionRelease> AllReleases => ExemptionRegistry.Registry.Values.SelectMany(series => series);

    public static TheoryData<string, string> Entries()
    {
        var data = new TheoryData<string, string>();
        foreach (var release in AllReleases)
        {
            foreach (var entry in release.Scheme.Entries)
            {
                data.Add(release.ReleaseId, entry.EntryId);
            }
        }

        return data;
    }

    // --- the committed registry loads, whole ---

    [Fact]
    public void The_launch_registry_holds_the_four_series() =>
        Assert.Equal(["exemptions/federal", "exemptions/fl", "exemptions/ga", "exemptions/tx"], ExemptionRegistry.SeriesIds());

    [Fact]
    public void Entry_ids_are_unique_within_each_release()
    {
        foreach (var release in AllReleases)
        {
            var ids = release.Scheme.Entries.Select(entry => entry.EntryId).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }

    [Fact]
    public void Release_ids_resolve_by_get_forever()
    {
        foreach (var release in AllReleases)
        {
            Assert.Same(release, ExemptionRegistry.Get(release.SeriesId, release.ReleaseId));
        }
    }

    // --- money ---

    [Theory]
    [MemberData(nameof(Entries))]
    public void Money_is_two_decimal_positive_strings(string releaseId, string entryId)
    {
        foreach (var money in MoneyFields(Entry(releaseId, entryId)))
        {
            var dot = money.IndexOf('.', StringComparison.Ordinal);
            Assert.True(dot > 0 && money[..dot].All(char.IsAsciiDigit), money);
            Assert.Equal(2, money[(dot + 1)..].Length);
            Assert.True(money[(dot + 1)..].All(char.IsAsciiDigit), money);
            Assert.True(Exact.Dec(money) > 0);
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Unlimited_and_amount_are_mutually_exclusive(string releaseId, string entryId)
    {
        var entry = Entry(releaseId, entryId);
        if (entry.Unlimited)
        {
            Assert.Null(entry.Amount);
            Assert.Null(entry.PerItemAmount);
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void A_missing_cap_is_always_explained(string releaseId, string entryId)
    {
        var entry = Entry(releaseId, entryId);
        if (entry.Amount is null && !entry.Unlimited)
        {
            Assert.False(string.IsNullOrEmpty(entry.Notes), entry.EntryId);
        }
    }

    // --- verification and sources ---

    [Theory]
    [MemberData(nameof(Entries))]
    public void Every_entry_carries_a_citation_and_sources(string releaseId, string entryId)
    {
        var entry = Entry(releaseId, entryId);
        Assert.False(string.IsNullOrWhiteSpace(entry.Citation));
        Assert.False(string.IsNullOrWhiteSpace(entry.Description));
        Assert.NotEmpty(entry.Sources);
        foreach (var source in entry.Sources)
        {
            Assert.StartsWith("https://", source.Url, StringComparison.Ordinal);
            Assert.True(source.Accessed <= September1);
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void No_dollar_figure_is_unverified(string releaseId, string entryId)
    {
        var entry = Entry(releaseId, entryId);
        if (entry.Verification == Verification.Unverified)
        {
            Assert.Empty(MoneyFields(entry));
            Assert.Contains("UNVERIFIED", entry.Notes, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void A_figure_since_never_postdates_its_release(string releaseId, string entryId)
    {
        var entry = Entry(releaseId, entryId);
        if (entry.FigureSince is { } since)
        {
            Assert.True(since <= Release(releaseId).EffectiveDate, entry.EntryId);
        }
    }

    // --- wildcard carryover links ---

    [Theory]
    [MemberData(nameof(Entries))]
    public void Carryover_link_and_cap_travel_together(string releaseId, string entryId)
    {
        var entry = Entry(releaseId, entryId);
        Assert.Equal(entry.WildcardCarryoverFrom is null, entry.WildcardCarryoverCap is null);
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Carryover_points_at_a_homestead_in_the_same_scheme(string releaseId, string entryId)
    {
        var entry = Entry(releaseId, entryId);
        if (entry.WildcardCarryoverFrom is null)
        {
            return;
        }

        var target = Release(releaseId).Scheme.Entries.FirstOrDefault(e => e.EntryId == entry.WildcardCarryoverFrom);
        Assert.NotNull(target);
        Assert.Equal(ExemptionCategory.Homestead, target.Category);
    }

    // --- the federal series ---

    [Fact]
    public void Federal_dollar_entries_carry_the_2025_adjustment_date()
    {
        var release = ExemptionRegistry.Resolve(ExemptionRegistry.FederalSeries, September1);
        Assert.Equal(new DateOnly(2025, 4, 1), release.EffectiveDate);
        foreach (var entry in release.Scheme.Entries)
        {
            if (entry.Amount is not null || entry.PerItemAmount is not null)
            {
                Assert.Equal(new DateOnly(2025, 4, 1), entry.FigureSince);
            }
        }
    }

    [Fact]
    public void The_next_federal_adjustment_is_scheduled()
    {
        Assert.Equal(new DateOnly(2028, 4, 1), ExemptionRegistry.NextFederalAdjustment);
        foreach (var limit in ExemptionRegistry.FederalLimits(September1))
        {
            Assert.Equal(ExemptionRegistry.NextFederalAdjustment, limit.NextAdjustment);
            Assert.NotEqual(Verification.Unverified, limit.Verification);
        }
    }

    [Fact]
    public void Only_the_federal_series_carries_the_522_caps()
    {
        foreach (var release in AllReleases)
        {
            if (release.SeriesId == ExemptionRegistry.FederalSeries)
            {
                Assert.Equal(
                    new HashSet<string> { "us-lien-avoidance-tools", "us-lien-avoidance-household", "us-ira-cap", "us-homestead-1215-day-cap", "us-homestead-misconduct-cap" },
                    release.Limits.Select(limit => limit.LimitId).ToHashSet());
            }
            else
            {
                Assert.Empty(release.Limits);
            }
        }
    }

    [Fact]
    public void The_federal_scheme_has_no_opt_out_axis()
    {
        var scheme = ExemptionRegistry.Resolve(ExemptionRegistry.FederalSeries, September1).Scheme;
        Assert.Null(scheme.OptedOutOfFederal);
        Assert.Null(scheme.OptOutCitation);
    }

    // --- scheme election (106C line 1) ---

    [Theory]
    [InlineData("FL")]
    [InlineData("GA")]
    public void Opt_out_states_offer_only_their_own_scheme(string state)
    {
        var schemes = ExemptionRegistry.SchemesForState(state, September1);
        Assert.Single(schemes);
        Assert.Equal(state, schemes[0].Jurisdiction);
        Assert.False(string.IsNullOrEmpty(schemes[0].OptOutCitation));
    }

    [Fact]
    public void Texas_offers_the_federal_election() =>
        Assert.Equal(["TX", "US"], ExemptionRegistry.SchemesForState("TX", September1).Select(scheme => scheme.Jurisdiction));

    [Fact]
    public void State_lookup_is_case_insensitive() =>
        Assert.Equal(ExemptionRegistry.SchemesForState("FL", September1), ExemptionRegistry.SchemesForState("fl", September1));

    [Fact]
    public void An_unsupported_state_is_refused_not_guessed() =>
        Assert.Contains("launch set", Assert.Throws<EngineKeyException>(() => ExemptionRegistry.SchemesForState("CA", September1)).Message, StringComparison.Ordinal);

    [Fact]
    public void Every_launch_state_has_a_scheme()
    {
        foreach (var state in ExemptionRegistry.LaunchStates)
        {
            Assert.NotEmpty(ExemptionRegistry.SchemesForState(state, September1));
        }
    }

    // --- resolution semantics ---

    [Fact]
    public void Georgia_resolves_to_the_hb1024_figures()
    {
        var homesteads = ExemptionRegistry.Resolve("exemptions/ga", September1).Scheme.Entries.Where(e => e.Category == ExemptionCategory.Homestead).ToList();
        Assert.Equal(["50000.00"], homesteads.Select(e => e.Amount));
        Assert.Equal(["100000.00"], homesteads.Select(e => e.JointAmount));
    }

    [Fact]
    public void Resolution_before_a_series_begins_refuses() =>
        Assert.Contains("refusing", Assert.Throws<EngineLookupException>(() => ExemptionRegistry.Resolve("exemptions/ga", new DateOnly(2026, 6, 30))).Message, StringComparison.Ordinal);

    [Fact]
    public void An_unknown_series_is_a_key_error() =>
        Assert.Contains("unknown series", Assert.Throws<EngineKeyException>(() => ExemptionRegistry.ReleasesOf("exemptions/nowhere")).Message, StringComparison.Ordinal);

    [Fact]
    public void Latest_returns_the_newest_release_even_if_future()
    {
        foreach (var seriesId in ExemptionRegistry.SeriesIds())
        {
            Assert.Same(ExemptionRegistry.ReleasesOf(seriesId)[^1], ExemptionRegistry.Latest(seriesId));
        }
    }

    [Fact]
    public void A_correction_wins_future_resolution_by_sequence()
    {
        var original = Stub(new DateOnly(2025, 4, 1), 1);
        var correction = Stub(new DateOnly(2025, 4, 1), 2);
        var picked = ExemptionRegistry.PickRelease([original, correction], new DateOnly(2025, 6, 1));
        Assert.Same(correction, picked);
        Assert.Equal("exemptions/stub@2025-04-01+2", picked.ReleaseId);
    }

    [Fact]
    public void Resolution_picks_the_greatest_effective_date_not_the_newest()
    {
        var older = Stub(new DateOnly(2025, 4, 1), 1);
        var newer = Stub(new DateOnly(2028, 4, 1), 1);
        Assert.Same(older, ExemptionRegistry.PickRelease([older, newer], new DateOnly(2027, 1, 1)));
    }

    // --- the loader rejects malformed releases ---

    [Fact]
    public void The_loader_accepts_a_well_formed_release() =>
        Assert.Equal("exemptions/stub@2025-04-01", ExemptionRegistry.LoadRegistry(Tree())["exemptions/stub"][0].ReleaseId);

    [Theory]
    [InlineData("""{"series_id": "exemptions/other"}""", "series_id")]
    [InlineData("""{"effective_date": "2025-05-01"}""", "disagrees with the path")]
    [InlineData("""{"sequence": 2}""", "sequence disagrees")]
    [InlineData("""{"notes": ""}""", "notes missing")]
    [InlineData("""{"source": null}""", "source missing")]
    public void The_loader_rejects_a_manifest_that_lies_about_its_path(string overrides, string problem) =>
        Assert.Contains(problem, Assert.Throws<MalformedReleaseException>(() => ExemptionRegistry.LoadRegistry(Tree(manifestOverrides: overrides))).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("""{"amount": "1,000.00"}""", "money")]
    [InlineData("""{"amount": "1000.0"}""", "money")]
    [InlineData("""{"amount": 1000}""", "money")]
    [InlineData("""{"category": "yacht"}""", "yacht")]
    [InlineData("""{"verification": "vibes"}""", "vibes")]
    [InlineData("""{"sources": []}""", "sources")]
    [InlineData("""{"citation": ""}""", "citation")]
    public void The_loader_rejects_a_malformed_figure(string overrides, string problem) =>
        Assert.Contains(problem, Assert.Throws<MalformedReleaseException>(() => ExemptionRegistry.LoadRegistry(Tree(entryOverrides: overrides))).Message, StringComparison.Ordinal);

    [Fact]
    public void The_loader_rejects_a_misnamed_release_directory() =>
        Assert.Contains("effective_date", Assert.Throws<MalformedReleaseException>(() => ExemptionRegistry.LoadRegistry(Tree(dirname: "april-2025"))).Message, StringComparison.Ordinal);

    private static ExemptionRelease Release(string releaseId) => AllReleases.Single(release => release.ReleaseId == releaseId);

    private static ExemptionEntry Entry(string releaseId, string entryId) => Release(releaseId).Scheme.Entries.Single(entry => entry.EntryId == entryId);

    private static List<string> MoneyFields(ExemptionEntry entry) =>
        [.. new[] { entry.Amount, entry.PerItemAmount, entry.JointAmount, entry.WildcardCarryoverCap }.OfType<string>()];

    private static ExemptionRelease Stub(DateOnly effective, int sequence) =>
        new("exemptions/stub", effective, sequence, "https://example.gov/", null, null, "stub", new ExemptionScheme("stub", "US", "stub", null, null, []), []);

    private static RegistryTree Tree(string dirname = "2025-04-01", string? manifestOverrides = null, string? entryOverrides = null)
    {
        var manifest = Trees.Merge(
            """{"series_id": "exemptions/stub", "effective_date": "2025-04-01", "sequence": 1, "source": {"url": "https://example.gov/", "published": null, "sha256": null}, "notes": "a well-formed stub release"}""",
            manifestOverrides);
        var entry = Trees.Merge(
            """{"entry_id": "stub-homestead", "category": "homestead", "description": "stub", "citation": "11 U.S.C. § 522(d)(1)", "amount": "31575.00", "unlimited": false, "verification": "primary", "sources": [{"title": "stub", "url": "https://example.gov/", "accessed": "2026-09-01"}]}""",
            entryOverrides);
        var payload = new JsonObject
        {
            ["scheme_id"] = "stub",
            ["jurisdiction"] = "US",
            ["name"] = "stub scheme",
            ["opted_out_of_federal"] = null,
            ["opt_out_citation"] = null,
            ["notes"] = string.Empty,
            ["entries"] = new JsonArray(entry),
            ["limits"] = new JsonArray(),
        };
        return new RegistryTree(new Dictionary<string, string>
        {
            [$"exemptions/stub/{dirname}/manifest.json"] = manifest.ToJsonString(),
            [$"exemptions/stub/{dirname}/scheme.json"] = payload.ToJsonString(),
        });
    }
}
