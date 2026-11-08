using System.Text.Json.Nodes;
using Insolvia.Engines.Regulatory;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_dollar_amounts.py: the loader
// against malformed releases (built as an in-memory tree, the tmp_path of the
// Python), and the committed registry swept and pinned to the Federal Register.
public sealed class DollarAmountsTests
{
    public static TheoryData<string> AmountIds()
    {
        var data = new TheoryData<string>();
        foreach (var release in DollarAmounts.All)
        {
            foreach (var amount in release.Amounts)
            {
                data.Add($"{release.ReleaseId}:{amount.AmountId}");
            }
        }

        return data;
    }

    // --- the committed registry loads, whole ---

    [Fact]
    public void The_committed_series_loads_and_names_itself() =>
        Assert.Equal(["code/dollar-amounts@2025-04-01"], DollarAmounts.All.Select(release => release.ReleaseId));

    [Fact]
    public void The_apr_2025_figures_match_the_federal_register()
    {
        var release = DollarAmounts.Get("code/dollar-amounts@2025-04-01");
        var expected = new Dictionary<string, string>
        {
            ["means-test-presumption-floor-60mo"] = "10275.00",
            ["means-test-presumption-ceiling-60mo"] = "17150.00",
            ["means-test-education-annual-cap-per-child"] = "2575.00",
            ["special-circumstances-presumption-floor-60mo"] = "10275.00",
            ["special-circumstances-presumption-ceiling-60mo"] = "17150.00",
            ["median-addon-per-person-motion-standing"] = "925.00",
            ["median-addon-per-person-safe-harbor"] = "925.00",
            ["attorney-sanctions-small-case-cap"] = "1725.00",
            ["sofa-payment-floor-business"] = "8575.00",
            ["sofa-payment-floor-consumer"] = "600.00",
        };
        Assert.Equal(expected, release.Amounts.ToDictionary(amount => amount.AmountId, amount => amount.Amount));
    }

    [Theory]
    [MemberData(nameof(AmountIds))]
    public void Every_figure_is_cited_verified_and_double_sourced(string id)
    {
        var amount = DollarAmounts.Get(id.Split(':')[0]).Amount(id.Split(':')[1]);
        Assert.NotEqual(Verification.Unverified, amount.Verification);
        Assert.Contains("U.S.C.", amount.Citation, StringComparison.Ordinal);
        Assert.True(amount.Sources.Count >= 2);
        Assert.True(amount.Value > 0);
    }

    [Fact]
    public void The_dataset_does_not_outlive_its_review_date()
    {
        Assert.Equal(new DateOnly(2028, 4, 1), DollarAmounts.NextSection104Adjustment);
        foreach (var amount in DollarAmounts.Latest().Amounts)
        {
            if (amount.NextAdjustment is { } next)
            {
                Assert.Equal(DollarAmounts.NextSection104Adjustment, next);
            }
        }

        Assert.True(DollarAmounts.Latest().EffectiveDate < DollarAmounts.NextSection104Adjustment);
    }

    [Fact]
    public void The_consumer_floor_is_the_one_unadjusted_figure()
    {
        var consumer = DollarAmounts.Latest().Amount("sofa-payment-floor-consumer");
        Assert.Null(consumer.NextAdjustment);
        Assert.Equal("11 U.S.C. § 547(c)(8)", consumer.Citation);
    }

    // --- resolution semantics ---

    [Fact]
    public void Resolution_before_the_series_begins_refuses()
    {
        var error = Assert.Throws<EngineLookupException>(() => DollarAmounts.Resolve(new DateOnly(2025, 3, 31)));
        Assert.Contains("refusing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_and_get_agree_on_the_current_release()
    {
        var release = DollarAmounts.Resolve(new DateOnly(2026, 9, 4));
        Assert.Same(release, DollarAmounts.Get(release.ReleaseId));
        Assert.Same(release, DollarAmounts.Latest());
    }

    [Fact]
    public void An_unknown_release_id_is_a_key_error()
    {
        var error = Assert.Throws<EngineKeyException>(() => DollarAmounts.Get("code/dollar-amounts@1999-01-01"));
        Assert.Contains("no release", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_correction_wins_future_resolution_by_sequence()
    {
        var original = Stub(new DateOnly(2025, 4, 1), 1);
        var correction = Stub(new DateOnly(2025, 4, 1), 2);
        var picked = DollarAmounts.PickRelease([original, correction], new DateOnly(2025, 6, 1));
        Assert.Same(correction, picked);
        Assert.Equal("code/dollar-amounts@2025-04-01+2", picked.ReleaseId);
    }

    // --- the loader rejects malformed releases ---

    [Fact]
    public void The_loader_accepts_a_well_formed_release()
    {
        var loaded = DollarAmounts.LoadRegistry(Release());
        Assert.Equal("code/dollar-amounts@2025-04-01", loaded[0].ReleaseId);
        Assert.Equal(10275.00m, loaded[0].Amount("stub-floor").Value);
    }

    [Theory]
    [InlineData("""{"series_id": "code/other"}""", "series_id")]
    [InlineData("""{"effective_date": "2025-05-01"}""", "disagrees with the path")]
    [InlineData("""{"sequence": 2}""", "sequence disagrees")]
    [InlineData("""{"notes": ""}""", "notes missing")]
    [InlineData("""{"source": null}""", "source missing")]
    public void The_loader_rejects_a_manifest_that_lies_about_its_path(string overrides, string problem)
    {
        var error = Assert.Throws<MalformedReleaseException>(() => DollarAmounts.LoadRegistry(Release(manifestOverrides: overrides)));
        Assert.Contains(problem, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"amount": "1,000.00"}""", "money")]
    [InlineData("""{"amount": "1000.0"}""", "money")]
    [InlineData("""{"amount": 1000}""", "money")]
    [InlineData("""{"verification": "vibes"}""", "vibes")]
    [InlineData("""{"verification": "unverified"}""", "never be unverified")]
    [InlineData("""{"sources": []}""", "sources")]
    [InlineData("""{"citation": ""}""", "citation")]
    [InlineData("""{"description": ""}""", "description")]
    public void The_loader_rejects_a_malformed_figure(string overrides, string problem)
    {
        var error = Assert.Throws<MalformedReleaseException>(() => DollarAmounts.LoadRegistry(Release(amountOverrides: overrides)));
        Assert.Contains(problem, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_loader_rejects_duplicate_amount_ids()
    {
        var error = Assert.Throws<MalformedReleaseException>(() => DollarAmounts.LoadRegistry(Release(duplicate: true)));
        Assert.Contains("duplicate amount ids", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_series_directory_is_refused()
    {
        var error = Assert.Throws<MalformedReleaseException>(() => DollarAmounts.LoadRegistry(new RegistryTree(new Dictionary<string, string>(), ["code/dollar-amounts"])));
        Assert.Contains("no releases", error.Message, StringComparison.Ordinal);
    }

    private static DollarAmountsRelease Stub(DateOnly effective, int sequence) =>
        new(DollarAmounts.SeriesId, effective, sequence, "https://example.gov/", null, null, "stub", []);

    private static RegistryTree Release(string? manifestOverrides = null, string? amountOverrides = null, bool duplicate = false)
    {
        var manifest = Trees.Merge(
            """{"series_id": "code/dollar-amounts", "effective_date": "2025-04-01", "sequence": 1, "source": {"url": "https://example.gov/", "published": null, "sha256": null}, "notes": "a well-formed stub release"}""",
            manifestOverrides);
        var amount = Trees.Merge(
            """{"amount_id": "stub-floor", "citation": "11 U.S.C. § 707(b)(2)(A)(i)(I)", "description": "stub", "amount": "10275.00", "verification": "primary", "sources": [{"title": "stub", "url": "https://example.gov/", "accessed": "2026-09-04"}], "figure_since": "2025-04-01", "next_adjustment": "2028-04-01"}""",
            amountOverrides);
        var amounts = new JsonArray(amount.DeepClone());
        if (duplicate)
        {
            amounts.Add(amount.DeepClone());
        }

        return new RegistryTree(new Dictionary<string, string>
        {
            ["code/dollar-amounts/2025-04-01/manifest.json"] = manifest.ToJsonString(),
            ["code/dollar-amounts/2025-04-01/amounts.json"] = new JsonObject { ["amounts"] = amounts }.ToJsonString(),
        });
    }
}

/// <summary>Builds the in-memory registry trees the loader tests feed the loaders.</summary>
internal static class Trees
{
    /// <summary>A JSON object literal with another's members laid over it (Python's <c>dict.update</c>).</summary>
    public static JsonObject Merge(string json, string? overrides)
    {
        var target = JsonNode.Parse(json)!.AsObject();
        if (overrides is not null)
        {
            foreach (var (key, value) in JsonNode.Parse(overrides)!.AsObject())
            {
                target[key] = value?.DeepClone();
            }
        }

        return target;
    }
}
