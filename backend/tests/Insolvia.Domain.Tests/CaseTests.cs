using Insolvia.Core.Cases;
using static Insolvia.Core.Tests.CaseFixtures;

namespace Insolvia.Core.Tests;

/// <summary>Case creation and the court reference, translated from test_cases.py.</summary>
public sealed class CaseTests
{
    [Fact]
    public void A_case_is_opened_for_one_client_or_two_in_filing_order()
    {
        Assert.Equal([ClientA], CaseRules.ParseCreation(Opening()).ClientIds);
        Assert.Equal([ClientA, ClientB], CaseRules.ParseCreation(Opening(ClientA, ClientB)).ClientIds);
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
        payload["client_ids"] = Body.Value(clientIds);
        var error = Assert.Throws<FieldValidationException>(() => CaseRules.ParseCreation(payload));
        Assert.Equal(["client_ids"], error.Fields.Keys);
    }

    [Fact]
    public void A_caller_with_no_request_behind_it_may_open_a_case_without_clients()
    {
        var draft = CaseRules.ParseCreation(Body.Of("""{"chapter": 7, "court": "flmb", "division": "tampa"}"""), requireClients: false);
        Assert.Empty(draft.ClientIds);
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
    [InlineData("""{"chapter": 7, "court": "flmb", "division": "tampa", "district": "x"}""", "district")]
    public void An_unknown_or_half_reference_is_refused_by_name(string payload, string field)
    {
        var error = Assert.Throws<FieldValidationException>(() => CaseRules.ParseCreation(Body.Of(payload)));
        Assert.Contains(field, error.Fields.Keys);
    }

    [Fact]
    public void A_court_change_rewrites_all_three_fields_together()
    {
        var (created, _) = NewCase();
        var moved = CaseRules.ApplyChanges(created, CaseRules.ParseUpdate(Body.Of("""{"court": "txsb", "division": "houston"}""")));
        Assert.Equal(("txsb", "houston", "Southern District of Texas"), (moved.Court, moved.Division, moved.District));
        var same = CaseRules.ApplyChanges(moved, new CaseChanges { Chapter = 13 });
        Assert.Equal(("txsb", "houston", "Southern District of Texas"), (same.Court, same.Division, same.District));
    }

    [Fact]
    public void A_patch_carrying_one_half_is_refused()
    {
        var error = Assert.Throws<FieldValidationException>(() => CaseRules.ParseUpdate(Body.Of("""{"division": "orlando"}""")));
        Assert.Contains("court", error.Fields.Keys);
    }

    // test_the_item_round_trips_the_reference and
    // test_a_row_written_before_the_registry_still_reads asserted on the
    // DynamoDB item (`case_item`/`case_from_item`); dropped — 18.10's mapping.

    // ── Beyond the Python suite: pins the parsers' edges ───────────────

    [Theory]
    [InlineData("true", "Chapter must be a number.")]
    [InlineData("7.0", "Chapter must be a number.")]
    [InlineData("9", "Chapter must be one of 7, 11, 12, 13.")]
    public void A_chapter_is_one_of_the_four_and_true_is_not_one(string chapter, string message)
    {
        var payload = Opening();
        payload["chapter"] = Body.Value(chapter);
        var error = Assert.Throws<FieldValidationException>(() => CaseRules.ParseCreation(payload));
        Assert.Equal(message, error.Fields["chapter"]);
    }

    [Fact]
    public void An_empty_patch_is_refused_rather_than_a_silent_no_op() =>
        Assert.Throws<ValidationException>(() => CaseRules.ParseUpdate(Body.Of("{}")));

    [Fact]
    public void A_meeting_before_the_petition_is_refused_when_both_arrive_together()
    {
        var error = Assert.Throws<FieldValidationException>(() =>
            CaseRules.ParseUpdate(Body.Of("""{"filed_at": "2026-09-02", "meeting_341_at": "2026-09-01"}""")));
        Assert.Equal(["meeting_341_at"], error.Fields.Keys);
    }

    [Fact]
    public void A_null_date_clears_and_touches_the_deadline_anchors()
    {
        var changes = CaseRules.ParseUpdate(Body.Of("""{"filed_at": null}"""));
        Assert.True(changes.ClearFiledAt);
        Assert.True(changes.TouchesDeadlineAnchors);
        Assert.False(CaseRules.ParseUpdate(Body.Of("""{"judge": "J"}""")).TouchesDeadlineAnchors);
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
