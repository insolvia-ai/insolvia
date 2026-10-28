using Insolvia.Engines.Projections;

namespace Insolvia.Engines.Tests;

// Translated from the committed-registry half of services/api/tests/unit/
// test_form_templates.py — the half the projections read (spec, AcroForm
// dump, resolution). The template PDFs, their sha256 check and the loader's
// malformed-release suite (which builds template.pdf files) move with the
// fill engine to Insolvia.Forms (18.16).
public sealed class FormTemplateTests
{
    private static IEnumerable<FormRelease> AllReleases => FormTemplates.Registry.Values.SelectMany(series => series);

    public static TheoryData<string> ReleaseIds() => [.. AllReleases.Select(release => release.ReleaseId)];

    [Fact]
    public void The_registry_holds_the_expected_series() =>
        Assert.Equal(
            [
                "form/b101", "form/b106ab", "form/b106c", "form/b106d", "form/b106dec", "form/b106ef", "form/b106g", "form/b106h", "form/b106i", "form/b106j",
                "form/b106j2", "form/b106sum", "form/b107", "form/b108", "form/b113", "form/b121", "form/b122a1", "form/b122a2", "form/b122c1", "form/b122c2",
                "form/b2010", "form/b2030",
            ],
            FormTemplates.SeriesIds());

    [Theory]
    [MemberData(nameof(ReleaseIds))]
    public void Every_release_names_the_official_source(string releaseId) =>
        Assert.StartsWith("https://www.uscourts.gov/", Release(releaseId).SourceUrl, StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(ReleaseIds))]
    public void Every_fillable_widget_is_claimed_exactly_once(string releaseId)
    {
        var release = Release(releaseId);
        var fillable = release.Widgets.Where(pair => pair.Value.Kind != "pushbutton").Select(pair => pair.Key).ToHashSet();
        Assert.False(fillable.Count > 0 && release.Boxes.Count > 0);
        var claimed = release.Fields.SelectMany(field => field.PdfNames).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(fillable.Union(release.Boxes.Keys).Order(StringComparer.Ordinal), claimed);
    }

    [Fact]
    public void The_two_directors_forms_are_flat_releases()
    {
        var notice = FormTemplates.Latest("form/b2010");
        Assert.True(notice.IsFlat);
        Assert.Empty(notice.Fields);
        Assert.Empty(notice.Boxes);
        var disclosure = FormTemplates.Latest("form/b2030");
        Assert.True(disclosure.IsFlat);
        Assert.Empty(disclosure.Widgets);
        Assert.Equal(["fee.agreed"], disclosure.Field("line_1_fee_agreed").PdfNames);
        var box = disclosure.Boxes["source_paid.debtor"];
        Assert.True(box.IsCheck);
        Assert.Equal((1, 13.44), (box.Page, box.H!.Value));
        Assert.False(disclosure.Boxes["fee.agreed"].IsCheck);
        Assert.False(FormTemplates.Latest("form/b108").IsFlat);
    }

    [Theory]
    [MemberData(nameof(ReleaseIds))]
    public void Radio_options_are_the_pdf_export_states(string releaseId)
    {
        var release = Release(releaseId);
        foreach (var field in release.Fields.Where(field => field.Type == "radio"))
        {
            Assert.Equal(field.PdfNames.SelectMany(name => release.Widgets[name].States).ToHashSet(), field.Options.Select(option => option.Value).ToHashSet());
        }
    }

    [Fact]
    public void Widget_metadata_survives_loading()
    {
        var b101 = FormTemplates.Latest("form/b101");
        var district = b101.Widgets["Bankruptcy District Information"];
        Assert.Equal("choice", district.Kind);
        Assert.Contains("Middle District of Florida", district.ChoiceOptions);
        Assert.Equal(4, b101.Widgets["Debtor1.SSNum"].MaxLen);
        Assert.Equal(2, b101.Widgets["Debtor1.State"].MaxLen);
    }

    [Fact]
    public void Field_lookup_by_id()
    {
        var release = FormTemplates.Latest("form/b101");
        Assert.Equal("district", release.Field("caption.district").Type);
        Assert.Throws<EngineKeyException>(() => release.Field("no_such_field"));
    }

    [Fact]
    public void Resolve_picks_the_release_in_force()
    {
        var release = FormTemplates.Resolve("form/b101", new DateOnly(2026, 9, 1));
        Assert.Equal("form/b101@2024-06-22", release.ReleaseId);
        Assert.Equal("06/24", release.Revision);
    }

    [Fact]
    public void Resolution_refuses_dates_before_the_series_begins() =>
        Assert.Contains("refusing", Assert.Throws<EngineLookupException>(() => FormTemplates.Resolve("form/b101", new DateOnly(2020, 1, 1))).Message, StringComparison.Ordinal);

    [Fact]
    public void Get_returns_a_pinned_release_forever()
    {
        Assert.Equal(new DateOnly(2015, 12, 1), FormTemplates.Get("form/b106i", "form/b106i@2015-12-01").EffectiveDate);
        Assert.Throws<EngineKeyException>(() => FormTemplates.Get("form/b106i", "form/b106i@1999-01-01"));
    }

    [Fact]
    public void Unknown_series_is_a_key_error() => Assert.Throws<EngineKeyException>(() => FormTemplates.ReleasesOf("form/b999"));

    [Fact]
    public void Corrections_win_ties_by_sequence()
    {
        var baseline = FormTemplates.Latest("form/b101");
        var correction = baseline with { Sequence = 2 };
        var picked = Regulatory.Releases.Pick([baseline, correction], new DateOnly(2026, 1, 1), "refusing");
        Assert.Equal(2, picked.Sequence);
        Assert.Equal("form/b101@2024-06-22+2", picked.ReleaseId);
        Assert.Equal("2024-06-22+2", picked.Pin);
    }

    [Fact]
    public void Form_revisions_as_of_is_the_case_pin_map()
    {
        var pins = FormTemplates.RevisionsAsOf(new DateOnly(2026, 9, 1));
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["form/b101"] = "2024-06-22", ["form/b106ab"] = "2015-12-01", ["form/b106c"] = "2025-04-01", ["form/b106d"] = "2015-12-01",
                ["form/b106dec"] = "2015-12-01", ["form/b106ef"] = "2015-12-01", ["form/b106g"] = "2015-12-01", ["form/b106h"] = "2015-12-01",
                ["form/b106i"] = "2015-12-01", ["form/b106j"] = "2015-12-01", ["form/b106j2"] = "2015-12-01", ["form/b106sum"] = "2015-12-01",
                ["form/b107"] = "2025-04-01", ["form/b108"] = "2015-12-01", ["form/b113"] = "2017-12-01", ["form/b121"] = "2015-12-01",
                ["form/b122a1"] = "2019-12-01", ["form/b122a2"] = "2025-04-01", ["form/b122c1"] = "2019-10-01", ["form/b122c2"] = "2025-04-01",
                ["form/b2010"] = "2020-12-01", ["form/b2030"] = "2025-12-01",
            },
            pins);
        foreach (var (seriesId, pin) in pins)
        {
            Assert.Equal(pin, FormTemplates.Get(seriesId, $"{seriesId}@{pin}").Pin);
        }
    }

    [Fact]
    public void Every_registered_release_has_a_projection()
    {
        foreach (var seriesId in FormTemplates.SeriesIds())
        {
            var release = FormTemplates.Latest(seriesId);
            Assert.True(FormProjections.Projections.ContainsKey((release.SeriesId, release.Pin)), release.ReleaseId);
        }
    }

    private static FormRelease Release(string releaseId) => AllReleases.Single(release => release.ReleaseId == releaseId);
}
