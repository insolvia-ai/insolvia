using System.Text.Json.Nodes;

namespace Insolvia.Forms.Tests.Specs;

// One red case per check forms/scripts/check.py made: each starts from a
// small spec that passes and breaks exactly one thing, so a check that stops
// firing turns its case red instead of letting a bad spec through.
public sealed class FormSpecCheckerTests
{
    // A fillable form: a text widget, a one-state checkbox, a two-state radio,
    // a signature on page 2, and a pushbutton nothing needs to claim.
    private const string FillableDump = """
        {"form": "t1", "revision": "1/25", "effective_date": "2025-01-01", "pages": 2, "fields": [
          {"name": "Name", "kind": "text", "pages": [1]},
          {"name": "Box", "kind": "checkbox", "pages": [1], "states": ["On"]},
          {"name": "Choice", "kind": "radio", "pages": [1], "states": ["A", "B"]},
          {"name": "Sig", "kind": "text", "pages": [2]},
          {"name": "Print", "kind": "pushbutton", "pages": [1]}
        ]}
        """;

    private const string FillableSpec = """
        {"form": "t1", "official_number": "1", "title": "Test", "revision": "1/25", "effective_date": "2025-01-01",
         "signature_pages": [2], "parts": [{"number": 1, "title": "Part"}], "fields": [
          {"id": "name", "part": 1, "label": "Name", "type": "text", "maps_to": {"entity": "debtor", "attribute": "name"}, "pdf": {"names": ["Name"]}},
          {"id": "box", "label": "Box", "type": "checkbox", "maps_to": {"derived": "why"}, "pdf": {"names": ["Box"]}},
          {"id": "choice", "label": "Choice", "type": "radio", "options": [{"value": "A"}, {"value": "B", "label": "b"}], "maps_to": {"unmapped": "why"}, "pdf": {"pattern": "Choic."}},
          {"id": "sig", "label": "Signature", "type": "signature", "maps_to": {"constant": "why"}, "pdf": {"names": ["Sig"]}}
        ]}
        """;

    // A flat form (no AcroForm): a text box and a checkbox box, both overlays.
    private const string FlatDump = """
        {"form": "t1", "revision": "1/25", "effective_date": "2025-01-01", "pages": 1, "fields": []}
        """;

    private const string FlatSpec = """
        {"form": "t1", "official_number": "1", "title": "Test", "revision": "1/25", "effective_date": "2025-01-01",
         "signature_pages": [], "parts": [], "fields": [
          {"id": "caption", "label": "Caption", "type": "text", "maps_to": {"entity": "case", "attribute": "district"}, "pdf": {"overlay": [{"name": "a", "page": 1, "x": 1, "y": 1.5, "w": 10, "size": 7.6}]}},
          {"id": "tick", "label": "Tick", "type": "checkbox", "maps_to": {"derived": "why"}, "pdf": {"overlay": [{"name": "b", "page": 1, "x": 1, "y": 1, "w": 5, "h": 5}]}}
        ]}
        """;

    private sealed record Case(bool Flat, Action<JsonObject, JsonObject> Break, string Expected, bool DropDump = false);

    private static JsonObject Field(JsonObject spec, string id) =>
        spec["fields"]!.AsArray().OfType<JsonObject>().Single(field => (string?)field["id"] == id);

    private static JsonObject Box(JsonObject spec, string id) => Field(spec, id)["pdf"]!["overlay"]![0]!.AsObject();

    // script check -> the case that proves it. Names read as the defect.
    private static readonly Dictionary<string, Case> Cases = new(StringComparer.Ordinal)
    {
        // 1. Structure: the spec file
        ["a_top_level_key_is_missing"] = new(false, (s, _) => s.Remove("parts"), "missing top-level keys: ['parts']"),
        ["form_disagrees_with_the_file_name"] = new(false, (s, _) => s["form"] = "t2", "\"form\" (t2) must match the file name (t1)"),
        ["the_dump_is_missing"] = new(false, (_, _) => { }, "t1: no AcroForm dump at forms/acroform/t1.json", DropDump: true),
        ["revision_disagrees_with_the_dump"] = new(false, (s, _) => s["revision"] = "9/99", "revision 9/99 != dump revision 1/25"),
        ["effective_date_disagrees_with_the_dump"] = new(false, (s, _) => s["effective_date"] = "2099-01-01", "effective_date disagrees with the dump"),
        ["a_signature_page_is_not_a_positive_integer"] = new(false, (s, _) => s["signature_pages"] = new JsonArray(0), "signature_pages must be a list of positive integers"),
        ["a_signature_page_is_a_float"] = new(false, (s, _) => s["signature_pages"] = JsonNode.Parse("[2.0]"), "signature_pages must be a list of positive integers"),
        ["signature_pages_repeat_a_page"] = new(false, (s, _) => s["signature_pages"] = new JsonArray(2, 2), "signature_pages must be sorted, strictly increasing page numbers"),
        ["a_part_has_more_than_number_and_title"] = new(false, (s, _) => s["parts"]![0]!["extra"] = 1, "part must have exactly number+title"),

        // 1. Structure: each field
        ["a_field_is_missing_a_required_key"] = new(false, (s, _) => Field(s, "name").Remove("label"), "name: missing keys ['label']"),
        ["a_field_has_an_unknown_key"] = new(false, (s, _) => Field(s, "name")["colour"] = "red", "name: unknown keys ['colour']"),
        ["two_fields_share_an_id"] = new(false, (s, _) => Field(s, "box")["id"] = "name", "duplicate field id name"),
        ["a_field_id_is_not_lower_snake_dotted"] = new(false, (s, _) => Field(s, "name")["id"] = "Name-1", "field id not lower_snake dotted: Name-1"),
        ["a_field_type_is_unknown"] = new(false, (s, _) => Field(s, "name")["type"] = "currency", "name: unknown type currency"),
        ["a_field_names_an_undeclared_part"] = new(false, (s, _) => Field(s, "name")["part"] = 7, "name: part 7 not declared in parts"),
        ["maps_to_has_two_mappings"] = new(false, (s, _) => Field(s, "name")["maps_to"]!["derived"] = "why", "name: maps_to needs exactly one of ['constant', 'derived', 'entity', 'unmapped']"),
        ["maps_to_has_no_mapping"] = new(false, (s, _) => Field(s, "name")["maps_to"] = new JsonObject(), "name: maps_to needs exactly one of"),

        // 4. Vocabulary
        ["an_entity_is_not_in_the_case_data_model"] = new(false, (s, _) => Field(s, "name")["maps_to"]!["entity"] = "pet", "name: unknown entity pet"),
        ["an_entity_mapping_has_no_attribute"] = new(false, (s, _) => Field(s, "name")["maps_to"]!.AsObject().Remove("attribute"), "name: entity mapping needs an attribute"),
        ["a_reason_mapping_carries_other_keys"] = new(false, (s, _) => Field(s, "box")["maps_to"]!["notes"] = "x", "box: derived mapping takes no other keys"),
        ["a_reason_mapping_is_empty"] = new(false, (s, _) => Field(s, "box")["maps_to"]!["derived"] = "", "box: maps_to.derived must be a non-empty reason/description"),
        ["a_reason_mapping_is_not_a_string"] = new(false, (s, _) => Field(s, "box")["maps_to"]!["derived"] = true, "box: maps_to.derived must be a non-empty reason/description"),

        // 2. Coverage: claims resolve, once each, and cover every widget
        ["pdf_mixes_names_and_overlay"] = new(false, (s, _) => Field(s, "name")["pdf"]!["overlay"] = new JsonArray(), "name: pdf must be {names: [...]} and/or {pattern: ...}, or {overlay: [...]}"),
        ["pdf_is_empty"] = new(false, (s, _) => Field(s, "name")["pdf"] = new JsonObject(), "name: pdf must be"),
        ["a_claimed_name_is_not_in_the_official_form"] = new(false, (s, _) => Field(s, "name")["pdf"]!["names"] = new JsonArray("Name", "Nope"), "name: pdf field not in the official form: 'Nope'"),
        ["a_pattern_matches_nothing"] = new(false, (s, _) => Field(s, "choice")["pdf"]!["pattern"] = "Choice2", "choice: pattern matched nothing: 'Choice2'"),
        ["a_pattern_only_matches_part_of_a_name"] = new(false, (s, _) => Field(s, "choice")["pdf"]!["pattern"] = "Cho", "choice: pattern matched nothing: 'Cho'"),
        ["two_fields_claim_one_widget"] = new(false, (s, _) => s["fields"]!.AsArray().Add(JsonNode.Parse("""{"id": "name2", "label": "x", "type": "text", "maps_to": {"constant": "x"}, "pdf": {"names": ["Name"]}}""")), "pdf field 'Name' claimed by both name and name2"),
        ["a_widget_is_not_claimed"] = new(false, (_, d) => d["fields"]!.AsArray().Add(JsonNode.Parse("""{"name": "Orphan", "kind": "text", "pages": [1]}""")), "1 official PDF fields not covered by any spec field: ['Orphan']"),

        // 3. Consistency: type against widget kind, options against states
        ["a_button_field_claims_a_text_widget"] = new(false, (s, d) => { d["fields"]!.AsArray().Add(JsonNode.Parse("""{"name": "Extra", "kind": "text", "pages": [1]}""")); Field(s, "box")["pdf"]!["names"] = new JsonArray("Box", "Extra"); }, "box: type checkbox but claims non-button widgets (['checkbox', 'text'])"),
        ["a_text_field_claims_a_button_widget"] = new(false, (s, d) => { d["fields"]!.AsArray().Add(JsonNode.Parse("""{"name": "Extra", "kind": "checkbox", "pages": [1], "states": ["On"]}""")); Field(s, "name")["pdf"]!["names"] = new JsonArray("Name", "Extra"); }, "name: type text but claims button widgets"),
        ["radio_options_miss_a_PDF_state"] = new(false, (s, _) => Field(s, "choice")["options"]!.AsArray().RemoveAt(1), "choice: options ['A'] != PDF states ['A', 'B']"),
        ["radio_options_add_a_state_the_PDF_lacks"] = new(false, (s, _) => Field(s, "choice")["options"]!.AsArray().Add(JsonNode.Parse("""{"value": "C"}""")), "choice: options ['A', 'B', 'C'] != PDF states ['A', 'B']"),
        ["a_checkbox_claims_a_multi_state_widget"] = new(false, (_, d) => d["fields"]![1]!["states"] = new JsonArray("On", "Off"), "box: 'Box' has states ['On', 'Off']; model it as a radio"),
        ["a_text_field_has_options"] = new(false, (s, _) => Field(s, "name")["options"] = JsonNode.Parse("""[{"value": "A"}]"""), "name: options only belong on radio/checkbox fields"),
        ["an_option_has_an_unknown_key"] = new(false, (s, _) => Field(s, "choice")["options"]![0]!["colour"] = "red", "choice: option has unknown keys"),
        ["signature_pages_miss_a_signature_widget_page"] = new(false, (s, _) => s["signature_pages"] = new JsonArray(1), "signature_pages [1] != pages carrying a signature-type field [2]"),
        ["signature_pages_list_a_page_with_no_signature"] = new(false, (s, _) => s["signature_pages"] = new JsonArray(1, 2), "signature_pages [1, 2] != pages carrying a signature-type field [2]"),

        // 5. Flat forms: overlay boxes
        ["a_fillable_form_claims_an_overlay"] = new(false, (s, _) => Field(s, "name")["pdf"] = JsonNode.Parse("""{"overlay": [{"name": "a", "page": 1, "x": 1, "y": 1, "w": 1}]}"""), "name: overlay boxes are only for a form the PDF cannot fill; this one has widgets"),
        ["a_flat_form_field_claims_widget_names"] = new(true, (s, _) => { Field(s, "caption")["pdf"] = JsonNode.Parse("""{"names": ["x"]}"""); s["fields"]!.AsArray().Remove(Field(s, "tick")); }, "a flat form's fields must claim overlay boxes"),
        ["a_flat_form_has_a_radio"] = new(true, (s, _) => Field(s, "tick")["type"] = "radio", "tick: a flat form has no export states — model the choice as checkbox boxes"),
        ["an_overlay_is_empty"] = new(true, (s, _) => Field(s, "caption")["pdf"]!["overlay"] = new JsonArray(), "caption: overlay must be a non-empty list of boxes"),
        ["an_overlay_box_lacks_a_required_key"] = new(true, (s, _) => Box(s, "caption").Remove("w"), "caption: overlay box needs name, page, x, y, w"),
        ["an_overlay_box_has_an_unknown_key"] = new(true, (s, _) => Box(s, "caption")["colour"] = "red", "caption: overlay box has unknown keys: ['colour']"),
        ["an_overlay_box_name_is_empty"] = new(true, (s, _) => Box(s, "caption")["name"] = "", "caption: overlay box name must be a non-empty string"),
        ["two_overlay_boxes_share_a_name"] = new(true, (s, _) => Box(s, "tick")["name"] = "a", "overlay box 'a' claimed by both caption and tick"),
        ["an_overlay_box_page_is_past_the_end"] = new(true, (s, _) => Box(s, "caption")["page"] = 2, "caption: overlay box 'a' page 2 is not a page of the PDF"),
        ["an_overlay_box_page_is_zero"] = new(true, (s, _) => Box(s, "caption")["page"] = 0, "caption: overlay box 'a' page 0 is not a page of the PDF"),
        ["an_overlay_box_coordinate_is_negative"] = new(true, (s, _) => Box(s, "caption")["x"] = -1, "caption: overlay box 'a' x must be a non-negative number"),
        ["an_overlay_box_coordinate_is_a_boolean"] = new(true, (s, _) => Box(s, "caption")["y"] = true, "caption: overlay box 'a' y must be a non-negative number"),
        ["an_overlay_box_coordinate_is_a_string"] = new(true, (s, _) => Box(s, "caption")["w"] = "10", "caption: overlay box 'a' w must be a non-negative number"),
        ["a_text_overlay_box_carries_a_height"] = new(true, (s, _) => Box(s, "caption")["h"] = 5, "caption: overlay box 'a' carries h only when the field is a checkbox"),
        ["a_checkbox_overlay_box_has_no_height"] = new(true, (s, _) => Box(s, "tick").Remove("h"), "tick: overlay box 'b' carries h only when the field is a checkbox"),
        ["a_checkbox_overlay_box_has_a_size"] = new(true, (s, _) => Box(s, "tick")["size"] = 8, "tick: overlay box 'b' size is a positive point size, and only on a text box"),
        ["a_text_overlay_box_size_is_zero"] = new(true, (s, _) => Box(s, "caption")["size"] = 0, "caption: overlay box 'a' size is a positive point size, and only on a text box"),
        ["an_overlay_field_has_options"] = new(true, (s, _) => Field(s, "tick")["options"] = JsonNode.Parse("""[{"value": "A"}]"""), "tick: options only belong on radio/checkbox widget fields"),
    };

    public static TheoryData<string> CaseNames { get; } = [.. Cases.Keys];

    [Fact]
    public void A_complete_fillable_spec_passes_and_the_pushbutton_needs_no_claim()
    {
        var result = FormSpecChecker.Check("t1", JsonNode.Parse(FillableSpec), JsonNode.Parse(FillableDump));

        Assert.Empty(result.Errors);
        Assert.Equal((4, 4, 1), (result.SpecFields, result.Covered, result.EntityMapped));
    }

    [Fact]
    public void A_complete_flat_spec_passes_and_counts_its_boxes()
    {
        var result = FormSpecChecker.Check("t1", JsonNode.Parse(FlatSpec), JsonNode.Parse(FlatDump));

        Assert.Empty(result.Errors);
        Assert.Equal((2, 2, 1), (result.SpecFields, result.Covered, result.EntityMapped));
    }

    [Fact]
    public void A_flat_notice_may_declare_no_fields()
    {
        var spec = JsonNode.Parse(FlatSpec)!.AsObject();
        spec["fields"] = new JsonArray();

        Assert.Empty(FormSpecChecker.Check("t1", spec, JsonNode.Parse(FlatDump)).Errors);
    }

    [Fact]
    public void A_dump_with_no_spec_is_reported()
    {
        Assert.Equal(
            ["b999: AcroForm dump has no spec in forms/specs/"],
            FormSpecChecker.DumpsWithoutSpec(["b101"], ["b101", "b999"]));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void A_broken_spec_is_refused(string name)
    {
        var (flat, breakIt, expected, dropDump) = Cases[name];
        var spec = JsonNode.Parse(flat ? FlatSpec : FillableSpec)!.AsObject();
        var dump = JsonNode.Parse(flat ? FlatDump : FillableDump)!.AsObject();
        breakIt(spec, dump);

        var errors = FormSpecChecker.Check("t1", spec, dropDump ? null : dump).Errors;

        Assert.Contains(errors, error => error.Contains(expected, StringComparison.Ordinal));
    }
}
