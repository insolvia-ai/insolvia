using System.Text.Json.Nodes;
using Insolvia.Core.Provenance;

namespace Insolvia.Core.Tests;

// Translated from packages/insolvia_core/tests/unit/test_provenance.py: the
// two invariants from docs/reference/case-data-model.md and the path walker
// they stand on. Each test names a way the rule could be EVADED.
public sealed class ProvenanceTests
{
    private const string Confirmed = """{"source": "ai_extracted", "confirmed_by": "staff-1", "confirmed_at": "2026-08-05T12:00:00.000000Z"}""";

    private static Dictionary<string, ProvenanceEntry> Typed(params string[] paths) =>
        paths.ToDictionary(path => path, _ => new ProvenanceEntry(ProvenanceSource.StaffTyped), StringComparer.Ordinal);

    private static JsonObject ConfirmedWith(string extraMembers)
    {
        var entry = Body.Of(Confirmed);
        foreach (var (key, value) in Body.Of(extraMembers))
        {
            entry[key] = value?.DeepClone();
        }

        return entry;
    }

    private static JsonObject Map(string path, JsonNode entry) => new() { [path] = entry };

    private static IReadOnlyDictionary<string, string> Refused(Action act) =>
        Assert.Throws<FieldValidationException>(act).Fields;

    // ── populated_paths ─────────────────────────────────────────────

    [Fact]
    public void Absent_values_are_not_populated() =>
        Assert.Empty(ProvenanceRules.PopulatedPaths(Body.Of("""{"a": null, "b": "", "c": [], "d": {}}""")));

    [Fact]
    public void False_and_zero_are_answers_not_absences() =>
        Assert.Equal(
            ["dependents", "rents_residence"],
            ProvenanceRules.PopulatedPaths(Body.Of("""{"rents_residence": false, "dependents": 0}""")).Order(StringComparer.Ordinal));

    [Fact]
    public void Nested_maps_produce_dotted_paths() =>
        Assert.Equal(["name.given"], ProvenanceRules.PopulatedPaths(Body.Of("""{"name": {"given": "Ada", "surname": null}}""")));

    [Fact]
    public void List_elements_are_addressed_by_id_not_position() =>
        Assert.Equal(
            ["other_names_used[n1].surname"],
            ProvenanceRules.PopulatedPaths(Body.Of("""{"other_names_used": [{"id": "n1", "surname": "Byron"}]}""")));

    [Fact]
    public void Reordering_a_list_does_not_move_a_path()
    {
        var first = Body.Of("""{"aliases": [{"id": "a", "surname": "X"}, {"id": "b", "surname": "Y"}]}""");
        var second = Body.Of("""{"aliases": [{"id": "b", "surname": "Y"}, {"id": "a", "surname": "X"}]}""");
        Assert.Equal(
            ProvenanceRules.PopulatedPaths(first).Order(StringComparer.Ordinal),
            ProvenanceRules.PopulatedPaths(second).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_list_without_element_ids_is_attributed_whole() =>
        Assert.Equal(["employer_ids"], ProvenanceRules.PopulatedPaths(Body.Of("""{"employer_ids": ["12-3456789"]}""")));

    // ── parse_provenance ────────────────────────────────────────────

    [Fact]
    public void Absent_provenance_is_an_empty_map_not_an_error() => Assert.Empty(ProvenanceRules.Parse(null));

    [Fact]
    public void Rejects_an_unknown_source() =>
        Assert.Contains(
            "provenance.name.given.source",
            Refused(() => ProvenanceRules.Parse(Body.Of("""{"name.given": {"source": "vibes"}}"""))).Keys);

    [Fact]
    public void Rejects_a_key_that_is_not_a_field_path() =>
        Assert.Contains(
            "provenance.Name..given",
            Refused(() => ProvenanceRules.Parse(Body.Of("""{"Name..given": {"source": "staff_typed"}}"""))).Keys);

    [Fact]
    public void Accepts_a_bracketed_element_path() =>
        Assert.True(ProvenanceRules.Parse(Body.Of("""{"other_names_used[n1].surname": {"source": "staff_typed"}}""")).ContainsKey("other_names_used[n1].surname"));

    [Fact]
    public void Confidence_must_be_a_number_in_range()
    {
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(Map("a", ConfirmedWith("""{"confidence": 1.5}"""))));
        var entries = ProvenanceRules.Parse(Map("a", ConfirmedWith("""{"confidence": 0.9}""")));
        Assert.Equal(0.9, entries["a"].Confidence);
    }

    // ── Invariant 2: confirm before entry ───────────────────────────

    [Theory]
    [InlineData("ai_extracted")]
    [InlineData("imported")]
    [InlineData("client_answered")]
    public void Unconfirmed_machine_value_is_rejected(string source) =>
        Assert.Contains(
            "provenance.name.given",
            Refused(() => ProvenanceRules.Parse(Map("name.given", new JsonObject { ["source"] = source }))).Keys);

    [Fact]
    public void Imported_is_not_a_way_around_it() =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(Body.Of("""{"a": {"source": "imported", "confidence": 1.0}}""")));

    [Fact]
    public void A_timestamp_with_nobody_attached_is_not_a_confirmation() =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(
            Body.Of("""{"a": {"source": "ai_extracted", "confirmed_at": "2026-08-05T12:00:00.000000Z"}}""")));

    [Fact]
    public void A_person_with_no_moment_is_not_a_confirmation_either() =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(
            Body.Of("""{"a": {"source": "ai_extracted", "confirmed_by": "staff-1"}}""")));

    [Fact]
    public void A_confirmed_machine_value_is_accepted() =>
        Assert.Equal("staff-1", ProvenanceRules.Parse(Map("name.given", Body.Of(Confirmed)))["name.given"].ConfirmedBy);

    [Fact]
    public void Staff_typed_needs_no_confirmation() =>
        Assert.Null(ProvenanceRules.Parse(Body.Of("""{"a": {"source": "staff_typed"}}"""))["a"].ConfirmedAt);

    // ── Invariant 1: every value has provenance ─────────────────────

    [Fact]
    public void A_value_without_provenance_is_rejected() =>
        Assert.Contains(
            "provenance.name.given",
            Refused(() => ProvenanceRules.RequireProvenance(Body.Of("""{"name": {"given": "Ada"}}"""), Typed())).Keys);

    [Fact]
    public void Omitting_the_key_is_not_a_way_to_smuggle_a_value_in() =>
        Assert.Throws<FieldValidationException>(() =>
            ProvenanceRules.RequireProvenance(Body.Of("""{"name": {"given": "extracted, unconfirmed"}}"""), Typed()));

    [Fact]
    public void An_empty_record_needs_no_provenance() =>
        ProvenanceRules.RequireProvenance(Body.Of("""{"name": {"given": null}, "phone": ""}"""), Typed());

    [Fact]
    public void Server_owned_identity_is_exempt() =>
        ProvenanceRules.RequireProvenance(Body.Of("""{"id": "c1", "case_id": "c1", "name": {"given": "Ada"}}"""), Typed("name.given"));

    [Fact]
    public void Provenance_for_a_field_that_is_not_there_does_not_satisfy_it() =>
        Assert.Contains(
            "provenance.name.given",
            Refused(() => ProvenanceRules.RequireProvenance(Body.Of("""{"name": {"given": "Ada"}}"""), Typed("name.surname"))).Keys);

    [Fact]
    public void A_fully_covered_record_passes() =>
        ProvenanceRules.RequireProvenance(
            Body.Of("""{"name": {"given": "Ada", "surname": "Lovelace"}, "rents_residence": false}"""),
            Typed("name.given", "name.surname", "rents_residence"));

    // ── provenance_json ─────────────────────────────────────────────

    [Fact]
    public void Drops_null_members_rather_than_storing_them() =>
        Assert.True(JsonNode.DeepEquals(Body.Of("""{"a": {"source": "staff_typed"}}"""), ProvenanceRules.ToJson(Typed("a"))));

    [Fact]
    public void Keeps_every_member_that_has_a_value()
    {
        var entries = ProvenanceRules.Parse(Map("a", ConfirmedWith("""{"confidence": 0.5, "document_id": "d1"}""")));
        Assert.True(JsonNode.DeepEquals(
            Body.Of("""
                {"source": "ai_extracted", "confirmed_by": "staff-1", "confirmed_at": "2026-08-05T12:00:00.000000Z",
                 "document_id": "d1", "confidence": 0.5}
                """),
            ProvenanceRules.ToJson(entries)["a"]));
    }

    [Fact]
    public void Round_trips_through_parse()
    {
        var entries = ProvenanceRules.Parse(Map("a", Body.Of(Confirmed)));
        Assert.Equal(entries, ProvenanceRules.Parse(ProvenanceRules.ToJson(entries)));
    }

    // ── Evasions found in review ────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_confirmer_is_nobody(string blank) =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(
            Map("a", ConfirmedWith(new JsonObject { ["confirmed_by"] = blank }.ToJsonString()))));

    [Theory]
    [InlineData("0000-00-00T........Z")]
    [InlineData("9999-99-99T99:99:99Z")]
    [InlineData("2026-08-05T.Z")]
    public void A_timestamp_must_be_a_real_instant(string fake) =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(
            Map("a", ConfirmedWith(new JsonObject { ["confirmed_at"] = fake }.ToJsonString()))));

    [Theory]
    [InlineData("""{"created_at": {"ssn": "smuggled"}}""")]
    [InlineData("""{"id": {"nested": {"ssn": "smuggled"}}}""")]
    public void Case_data_cannot_hide_under_a_server_owned_name(string record) =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.RequireProvenance(Body.Of(record), Typed()));

    [Fact]
    public void The_server_owned_scalars_are_still_exempt() =>
        ProvenanceRules.RequireProvenance(Body.Of("""{"id": "c1", "case_id": "c1", "created_at": "t"}"""), Typed());

    [Theory]
    [InlineData("SSN")]
    [InlineData("legalName")]
    [InlineData("1099_income")]
    [InlineData("case-number")]
    [InlineData("")]
    public void A_key_that_is_not_a_field_name_fails_loudly(string key) =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.RequireProvenance(new JsonObject { [key] = "value" }, Typed()));

    [Fact]
    public void A_value_under_the_empty_key_is_not_invisible() =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.RequireProvenance(Body.Of("""{"": "smuggled"}"""), Typed()));

    [Fact]
    public void A_literal_dotted_key_cannot_collide_with_a_nested_path() =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.RequireProvenance(Body.Of("""{"name.given": "forged"}"""), Typed("name.given")));

    [Fact]
    public void A_mixed_list_does_not_discard_the_paths_before_the_bad_element()
    {
        var goodFirst = Body.Of("""{"aliases": [{"id": "n1", "surname": "Byron"}, {"surname": "X"}]}""");
        var badFirst = Body.Of("""{"aliases": [{"surname": "X"}, {"id": "n1", "surname": "Byron"}]}""");
        Assert.Equal(["aliases"], ProvenanceRules.PopulatedPaths(goodFirst));
        Assert.Equal(["aliases"], ProvenanceRules.PopulatedPaths(badFirst));

        // And the whole list still needs an entry — it is not exempt.
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.RequireProvenance(goodFirst, Typed()));
    }

    [Fact]
    public void An_id_that_cannot_be_addressed_does_not_mint_an_illegal_path()
    {
        var record = Body.Of("""{"aliases": [{"id": "n.1", "surname": "Byron"}]}""");
        Assert.Equal(["aliases"], ProvenanceRules.PopulatedPaths(record));
        ProvenanceRules.Parse(StaffTypedFor(ProvenanceRules.PopulatedPaths(record)));
    }

    [Fact]
    public void Every_emitted_path_is_one_parse_provenance_accepts()
    {
        var record = Body.Of("""{"name": {"given": "Ada"}, "aliases": [{"id": "n1", "surname": "Byron"}], "rents_residence": false}""");
        var paths = ProvenanceRules.PopulatedPaths(record);
        var entries = ProvenanceRules.Parse(StaffTypedFor(paths));
        Assert.Equal(paths.Order(StringComparer.Ordinal), entries.Keys.Order(StringComparer.Ordinal));
    }

    // ── The locator ─────────────────────────────────────────────────

    [Fact]
    public void A_well_formed_locator_is_accepted()
    {
        var entries = ProvenanceRules.Parse(Map("a", ConfirmedWith(
            """{"locator": {"document_id": "d1", "page": 3, "region": {"x": 0.1, "y": 0.2, "width": 0.5, "height": 0.05}}}""")));
        Assert.NotNull(entries["a"].Locator);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void A_non_finite_number_is_refused(double bad)
    {
        // Python's stdlib JSON accepts `Infinity` and `NaN` as numbers; a
        // strict parser does not, so the value is built directly here.
        var entry = Body.Of(Confirmed);
        entry["locator"] = new JsonObject { ["region"] = new JsonObject { ["x"] = JsonValue.Create(bad) } };
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(Map("a", entry)));
    }

    [Fact]
    public void A_region_outside_the_page_is_refused() =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(Map("a", ConfirmedWith("""{"locator": {"region": {"x": 1.5}}}"""))));

    [Fact]
    public void Pages_are_one_based() =>
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(Map("a", ConfirmedWith("""{"locator": {"page": 0}}"""))));

    [Fact]
    public void A_non_finite_confidence_is_refused()
    {
        var entry = Body.Of(Confirmed);
        entry["confidence"] = JsonValue.Create(double.NaN);
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(Map("a", entry)));
    }

    // ── Server sources are kept, never minted ───────────────────────

    private const string Answered = """
        {"source": "client_answered", "confirmed_by": "00000000-0000-4000-8000-00000000a11c",
         "confirmed_at": "2099-01-01T00:00:00.000000Z", "extraction_id": "00000000-0000-4000-8000-0000000ca4d1"}
        """;

    private static void Check(string body, string entries, string? storedBody, string? storedEntries) =>
        ProvenanceRules.RequireServerSourcesKept(
            Body.Of(body),
            ProvenanceRules.Parse(Body.Of(entries)),
            storedBody is null ? null : Body.Of(storedBody),
            storedEntries is null ? null : ProvenanceRules.Parse(Body.Of(storedEntries)));

    [Fact]
    public void An_unchanged_answered_value_keeps_its_entry()
    {
        var entries = $$"""{"phone": {{Answered}}}""";
        Check("""{"phone": "555-0100"}""", entries, """{"phone": "555-0100"}""", entries);
    }

    [Fact]
    public void A_changed_value_cannot_keep_the_answered_entry()
    {
        var entries = $$"""{"phone": {{Answered}}}""";
        Assert.Contains("provenance.phone", Refused(() => Check("""{"phone": "555-0199"}""", entries, """{"phone": "555-0100"}""", entries)).Keys);
    }

    [Fact]
    public void A_create_cannot_claim_the_client_answered() =>
        Assert.Throws<FieldValidationException>(() => Check("""{"phone": "555-0100"}""", $$"""{"phone": {{Answered}}}""", null, null));

    // ── Wire values ─────────────────────────────────────────────────

    [Fact]
    public void Every_source_keeps_its_python_wire_value() =>
        Assert.Equal(
            ["staff_typed", "ai_extracted", "imported", "library", "client", "client_answered"],
            Wire.Names<ProvenanceSource>());

    private static JsonObject StaffTypedFor(IEnumerable<string> paths)
    {
        var map = new JsonObject();
        foreach (var path in paths)
        {
            map[path] = new JsonObject { ["source"] = "staff_typed" };
        }

        return map;
    }
}
