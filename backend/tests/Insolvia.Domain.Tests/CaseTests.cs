using Insolvia.Domain.Cases;
using static Insolvia.Domain.Tests.CaseFixtures;

namespace Insolvia.Domain.Tests;

/// <summary>Case creation and the court reference, translated from test_cases.py.</summary>
public sealed class CaseTests
{
    private static NewCase Opened(System.Text.Json.Nodes.JsonObject payload) => Body.Read<NewCase>(payload);

    private static CaseChanges Changes(string json) => Body.Read<CaseChanges>(Body.Of(json));

    [Fact]
    public void A_case_is_opened_for_one_client_or_two_in_filing_order()
    {
        Assert.Equal([ClientA], Opened(Opening()).ClientIds);
        Assert.Equal([ClientA, ClientB], Opened(Opening(ClientA, ClientB)).ClientIds);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"not-a-list\"")]
    [InlineData($"[\"{ClientA}\", \"{ClientB}\", \"third\"]")]
    [InlineData($"[\"{ClientA}\", 7]")]
    [InlineData("[\" \"]")]
    [InlineData($"[\"{ClientA}\", \"{ClientA}\"]")]
    public void Client_ids_are_required_and_must_be_one_or_two_distinct_ids(string clientIds)
    {
        var payload = Opening();
        payload["clientIds"] = Body.Value(clientIds);
        var error = Assert.Throws<FieldValidationException>(() => Opened(payload));
        Assert.StartsWith("clientIds", Assert.Single(error.Fields.Keys), StringComparison.Ordinal);
    }

    [Fact]
    public void A_case_names_its_court_and_the_district_is_derived()
    {
        var (created, _) = NewCase();
        Assert.Equal(("flmb", "tampa"), (created.Court, created.Division));
        Assert.Equal("Middle District of Florida", created.District);
    }

    [Theory]
    [InlineData("""{"chapter": 7}""", "court")]
    [InlineData("""{"chapter": 7, "court": "flmb"}""", "division")]
    [InlineData("""{"chapter": 7, "court": "nyeb", "division": "brooklyn"}""", "court")]
    [InlineData("""{"chapter": 7, "court": "flmb", "division": "miami"}""", "division")]
    public void An_unknown_or_half_reference_is_refused_by_name(string payload, string field)
    {
        var opening = Body.Of(payload);
        opening["clientIds"] = new System.Text.Json.Nodes.JsonArray(ClientA);
        var error = Assert.Throws<FieldValidationException>(() => Opened(opening));
        Assert.Contains(field, error.Fields.Keys);
    }

    [Fact]
    public void A_court_change_rewrites_all_three_fields_together()
    {
        var (created, _) = NewCase();
        var moved = CaseRules.ApplyChanges(created, Changes("""{"court": "txsb", "division": "houston"}"""));
        Assert.Equal(("txsb", "houston", "Southern District of Texas"), (moved.Court, moved.Division, moved.District));
        var same = CaseRules.ApplyChanges(moved, new CaseChanges { Chapter = 13 });
        Assert.Equal(("txsb", "houston", "Southern District of Texas"), (same.Court, same.Division, same.District));
    }

    [Fact]
    public void A_patch_carrying_one_half_is_refused()
    {
        var error = Assert.Throws<FieldValidationException>(() => Changes("""{"division": "orlando"}"""));
        Assert.Contains("court", error.Fields.Keys);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("7.5")]
    [InlineData("9")]
    public void A_chapter_is_one_of_the_four_and_true_is_not_one(string chapter)
    {
        var payload = Opening();
        payload["chapter"] = Body.Value(chapter);
        var error = Assert.Throws<FieldValidationException>(() => Opened(payload));
        Assert.Equal(["chapter"], error.Fields.Keys);
    }

    [Fact]
    public void An_empty_patch_is_empty() =>
        Assert.True(Changes("{}").IsEmpty);

    [Fact]
    public void A_meeting_before_the_petition_is_refused_when_both_arrive_together()
    {
        var error = Assert.Throws<FieldValidationException>(() =>
            Changes("""{"filedAt": "2026-09-02", "meeting341At": "2026-09-01"}"""));
        Assert.Equal(["meeting341At"], error.Fields.Keys);
    }

    [Fact]
    public void A_null_date_clears_and_touches_the_deadline_anchors()
    {
        var changes = Changes("""{"filedAt": null}""");
        Assert.True(changes.Names(nameof(CaseChanges.FiledAt)));
        Assert.Null(changes.FiledAt);
        Assert.False(changes.IsEmpty);
        Assert.True(changes.TouchesDeadlineAnchors);
        Assert.False(Changes("""{"judge": "J"}""").TouchesDeadlineAnchors);
        Assert.False(Changes("""{"judge": "J"}""").Names(nameof(CaseChanges.FiledAt)));
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData("", 50)]
    [InlineData("1", 1)]
    [InlineData("100", 100)]
    public void A_listing_limit_defaults_and_is_bounded(string? raw, int expected) =>
        Assert.Equal(expected, CaseRules.ParseListLimit(raw));

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("ten")]
    public void A_listing_limit_out_of_range_is_refused(string raw) =>
        Assert.Throws<ValidationException>(() => CaseRules.ParseListLimit(raw));

    [Fact]
    public void A_cursor_round_trips_under_its_index_and_is_refused_under_another()
    {
        var cursor = CaseRules.EncodeCursor(new Dictionary<string, string> { ["SK"] = "2026-01-01T00:00:00.000000Z#c1" }, CaseRules.IndexByFirm);
        Assert.Equal("2026-01-01T00:00:00.000000Z#c1", CaseRules.DecodeCursor(cursor, CaseRules.IndexByFirm)["SK"]);
        Assert.Throws<ValidationException>(() => CaseRules.DecodeCursor(cursor, CaseRules.IndexByAssignee));
        Assert.Throws<ValidationException>(() => CaseRules.DecodeCursor("not base64!", CaseRules.IndexByFirm));
    }

    [Fact]
    public void The_case_statuses_keep_their_wire_strings()
    {
        Assert.Equal(["intake", "ready_to_file", "filed", "discharged", "dismissed", "closed"], Wire.Names<CaseStatus>());
        Assert.Equal(["state_and_federal_nonbankruptcy", "federal"], Wire.Names<ExemptionSet>());
    }
}
