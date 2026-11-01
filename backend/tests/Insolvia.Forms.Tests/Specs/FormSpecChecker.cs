using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Insolvia.Forms.Tests.Specs;

/// <summary>
/// forms/scripts/check.py, ported (#585): validates one forms/specs/*.json
/// against its forms/acroform/*.json dump. What is enforced, and why:
/// <list type="number">
/// <item>Structure — every spec file has the required keys, every field has
/// exactly one mapping, every enum value comes from a closed set. This is the
/// schema; it lives here rather than in a JSON Schema file so there is one
/// validator, not two that drift.</item>
/// <item>Coverage — every fillable AcroForm field in the official PDF is
/// claimed by exactly one spec field, and every claim resolves: a field the
/// spec forgot fails the build. Pushbuttons (print/reset/save/attach) are UI
/// chrome, not data, and are exempt.</item>
/// <item>Consistency — a spec field's type must agree with the PDF widget
/// kind it claims, and a radio's options must be exactly the states the PDF
/// declares.</item>
/// <item>Vocabulary — <c>maps_to.entity</c> must name an entity from
/// docs/reference/case-data-model.md. <see cref="Entities"/> mirrors that
/// document's "Core entities" table; when the model gains or loses an entity,
/// update both.</item>
/// <item>Flat forms — a Director's Form published with NO AcroForm (B2010,
/// B2030) has nothing to claim, so its spec fields claim <c>overlay</c> boxes
/// instead: page positions, measured from the PDF's own operators, that the
/// fill engine draws text (or an X) onto. A box name is unique per form, its
/// page exists, and a checkbox box carries a height. Overlay claims are
/// allowed ONLY on a form whose dump has no fillable widget — a fillable form
/// is filled through its widgets, never painted over — and a flat form with
/// nothing to print at all (B2010 is a notice) may declare no fields.</item>
/// </list>
/// The messages are the script's, so a red run reads the same as it did.
/// </summary>
internal static partial class FormSpecChecker
{
    // Mirrors docs/reference/case-data-model.md § Core entities (case-scoped
    // types a form can draw from; document/extraction_candidate never feed a form).
    public static readonly IReadOnlySet<string> Entities = new HashSet<string>(StringComparer.Ordinal)
    {
        "case",
        "petition",
        "debtor",
        "prior_case",
        "related_case",
        "sole_proprietorship",
        "filing_professional",
        "creditor",
        "claim",
        "asset",
        "exemption",
        "contract_lease",
        "codebtor",
        "community_household_member",
        "employment",
        "pay_period_record",
        "other_income_record",
        "means_test_input",
        "plan",
        "income_summary",
        "household",
        "expense",
        "dependent",
        "sofa_entry",
    };

    public static readonly IReadOnlySet<string> FieldTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        // free-entry widgets (PDF kind: text or choice)
        "text",       // unconstrained text
        "money",      // fixed-scale decimal, 2 places (case-data-model value types)
        "integer",    // counts: number of years, percentages entered as whole numbers
        "date",       // calendar date, no time
        "year",       // four-digit year
        "state",      // two-letter state, usually a PDF choice widget
        "district",   // the bankruptcy-district choice widget in every caption
        "ssn_itin",   // social security / taxpayer id boxes
        "ein",        // employer id boxes
        "phone",
        "email",
        "signature",  // a signature line rendered as a text widget
        // button widgets (PDF kind: checkbox or radio)
        "checkbox",   // one on/off box
        "radio",      // one exclusive group; `options` lists its states
    };

    private static readonly string[] RequiredSpecKeys =
        ["form", "official_number", "title", "revision", "effective_date", "signature_pages", "parts", "fields"];
    private static readonly string[] RequiredFieldKeys = ["id", "label", "type", "maps_to", "pdf"];
    private static readonly HashSet<string> OptionalFieldKeys = ["part", "line", "options", "repeats", "notes"];
    private static readonly string[] MappingKeys = ["constant", "derived", "entity", "unmapped"];
    // One overlay box: where on which page the engine draws a value (PDF user
    // space, points, origin bottom-left; `y` is the baseline for text and the
    // bottom edge for a checkbox). `h` is required for a checkbox and forbidden
    // otherwise — text is drawn at a baseline, an X is drawn across a box.
    // `size` (optional, text boxes only) is the point size a value is drawn at
    // where the printed blank is too small for the engine's default — Official
    // Form 113's table cells are 7.6-point type.
    private static readonly HashSet<string> OverlayBoxKeys = ["name", "page", "x", "y", "w", "h", "size"];
    private static readonly string[] OverlayBoxRequiredKeys = ["name", "page", "x", "y", "w"];
    private static readonly HashSet<string> OptionKeys = ["value", "label", "maps_to_value", "notes"];

    [GeneratedRegex(@"\A[a-z0-9_.]+\z")]
    private static partial Regex FieldIdShape();

    /// <summary>The dump stems that have no spec beside them.</summary>
    public static IEnumerable<string> DumpsWithoutSpec(IEnumerable<string> specStems, IEnumerable<string> dumpStems) =>
        dumpStems.Except(specStems, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(stem => $"{stem}: AcroForm dump has no spec in forms/specs/");

    /// <summary>
    /// Checks one spec. <paramref name="dump"/> is null when the spec has no
    /// dump beside it. <paramref name="stem"/> is the spec's file name.
    /// </summary>
    public static FormCheckResult Check(string stem, JsonNode? spec, JsonNode? dump)
    {
        var errors = new List<string>();
        if (spec is not JsonObject specObject)
        {
            errors.Add($"{stem}: the spec is not a JSON object");
            return new FormCheckResult(0, 0, 0, errors);
        }

        var form = specObject["form"] is JsonValue formValue && formValue.TryGetValue<string>(out var formName) ? formName : stem;
        void Err(string message) => errors.Add($"{form}: {message}");

        var missing = RequiredSpecKeys.Where(key => !specObject.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            Err($"missing top-level keys: {Py(missing)}");
            return new FormCheckResult(0, 0, 0, errors);
        }
        if (form != stem)
        {
            Err($"\"form\" ({form}) must match the file name ({stem})");
        }

        if (dump is not JsonObject dumpObject)
        {
            Err($"no AcroForm dump at forms/acroform/{form}.json");
            return new FormCheckResult(0, 0, 0, errors);
        }

        if (!JsonNode.DeepEquals(specObject["revision"], dumpObject["revision"]))
        {
            Err($"revision {Show(specObject["revision"])} != dump revision {Show(dumpObject["revision"])}");
        }
        if (!JsonNode.DeepEquals(specObject["effective_date"], dumpObject["effective_date"]))
        {
            Err("effective_date disagrees with the dump");
        }

        var sigPages = new List<long>();
        if (specObject["signature_pages"] is not JsonArray sigArray
            || !sigArray.All(page => Integer(page) is > 0))
        {
            Err("signature_pages must be a list of positive integers");
        }
        else
        {
            sigPages = [.. sigArray.Select(page => Integer(page)!.Value)];
            if (!sigPages.SequenceEqual(sigPages.Distinct().Order()))
            {
                Err("signature_pages must be sorted, strictly increasing page numbers");
            }
        }

        // pdf field name -> its dump entry, pushbuttons excluded.
        var pdfFields = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in (dumpObject["fields"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Text(entry["kind"]) != "pushbutton" && Text(entry["name"]) is { } name)
            {
                pdfFields[name] = entry;
            }
        }
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal); // pdf field name -> spec field id
        var overlayBoxes = new Dictionary<string, string>(StringComparer.Ordinal); // overlay box name -> spec field id (flat forms)
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var mapped = 0;
        var actualSigPages = new SortedSet<long>();
        var dumpPages = Integer(dumpObject["pages"]) ?? 0;

        var partNumbers = new List<JsonNode?>();
        foreach (var part in specObject["parts"] as JsonArray ?? [])
        {
            if (part is not JsonObject partObject || !partObject.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(["number", "title"]))
            {
                Err($"part must have exactly number+title: {Show(part)}");
                continue;
            }
            partNumbers.Add(partObject["number"]);
        }

        var fields = specObject["fields"] as JsonArray ?? [];
        foreach (var fieldNode in fields)
        {
            if (fieldNode is not JsonObject field)
            {
                Err($"field is not a JSON object: {Show(fieldNode)}");
                continue;
            }
            var fid = Text(field["id"]) ?? "<missing id>";
            var keys = field.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var missingFieldKeys = RequiredFieldKeys.Where(key => !keys.Contains(key)).Order(StringComparer.Ordinal).ToList();
            if (missingFieldKeys.Count > 0)
            {
                Err($"{fid}: missing keys {Py(missingFieldKeys)}");
                continue;
            }
            var extra = keys.Where(key => !RequiredFieldKeys.Contains(key) && !OptionalFieldKeys.Contains(key)).Order(StringComparer.Ordinal).ToList();
            if (extra.Count > 0)
            {
                Err($"{fid}: unknown keys {Py(extra)}");
            }
            if (!ids.Add(fid))
            {
                Err($"duplicate field id {fid}");
            }
            if (!FieldIdShape().IsMatch(fid))
            {
                Err($"field id not lower_snake dotted: {fid}");
            }

            var ftype = Text(field["type"]);
            if (ftype is null || !FieldTypes.Contains(ftype))
            {
                Err($"{fid}: unknown type {Show(field["type"])}");
            }
            if (field["part"] is { } part && !partNumbers.Any(number => JsonNode.DeepEquals(number, part)))
            {
                Err($"{fid}: part {Show(part)} not declared in parts");
            }

            // mapping
            var mapsTo = field["maps_to"] as JsonObject ?? [];
            var mkeys = MappingKeys.Where(mapsTo.ContainsKey).ToList();
            if (mkeys.Count != 1)
            {
                Err($"{fid}: maps_to needs exactly one of {Py(MappingKeys)}");
            }
            else if (mkeys[0] == "entity")
            {
                if (Text(mapsTo["entity"]) is not { } entity || !Entities.Contains(entity))
                {
                    Err($"{fid}: unknown entity {Show(mapsTo["entity"])}");
                }
                if (!Truthy(mapsTo["attribute"]))
                {
                    Err($"{fid}: entity mapping needs an attribute");
                }
                mapped++;
            }
            else
            {
                if (mapsTo.Count != 1)
                {
                    Err($"{fid}: {mkeys[0]} mapping takes no other keys");
                }
                if (Text(mapsTo[mkeys[0]]) is not { Length: > 0 })
                {
                    Err($"{fid}: maps_to.{mkeys[0]} must be a non-empty reason/description");
                }
            }

            // resolve pdf claims: exact names and/or a regex over dump names —
            // or, on a flat form, overlay boxes (rule 5 above)
            if (field["pdf"] is not JsonObject pdf || pdf.Count == 0
                || !(pdf.All(p => p.Key is "names" or "pattern") || (pdf.Count == 1 && pdf.ContainsKey("overlay"))))
            {
                Err($"{fid}: pdf must be {{names: [...]}} and/or {{pattern: ...}}, or {{overlay: [...]}}");
                continue;
            }
            if (pdf.ContainsKey("overlay"))
            {
                CheckOverlay(fid, ftype, field, pdf, pdfFields.Count > 0, dumpPages, overlayBoxes, Err);
                continue;
            }

            var matched = new List<string>();
            foreach (var nameNode in pdf["names"] as JsonArray ?? [])
            {
                if (Text(nameNode) is { } name && pdfFields.ContainsKey(name))
                {
                    matched.Add(name);
                }
                else
                {
                    Err($"{fid}: pdf field not in the official form: {ShowRepr(nameNode)}");
                }
            }
            if (pdf.ContainsKey("pattern"))
            {
                var pattern = Text(pdf["pattern"]) ?? "";
                // Python's re.fullmatch: the whole name, nothing either side.
                var rx = new Regex($@"\A(?:{pattern})\z", RegexOptions.CultureInvariant);
                var hits = pdfFields.Keys.Where(name => rx.IsMatch(name)).ToList();
                if (hits.Count == 0)
                {
                    Err($"{fid}: pattern matched nothing: {Repr(pattern)}");
                }
                matched.AddRange(hits);
            }
            foreach (var name in matched)
            {
                if (claimed.TryGetValue(name, out var other))
                {
                    Err($"pdf field {Repr(name)} claimed by both {other} and {fid}");
                }
                claimed[name] = fid;
            }

            if (ftype == "signature")
            {
                foreach (var name in matched)
                {
                    foreach (var page in pdfFields[name]["pages"] as JsonArray ?? [])
                    {
                        if (Integer(page) is { } number)
                        {
                            actualSigPages.Add(number);
                        }
                    }
                }
            }

            // type <-> widget kind agreement, and radio option/state agreement
            var kinds = matched.Select(name => Text(pdfFields[name]["kind"]) ?? "").ToHashSet(StringComparer.Ordinal);
            if (ftype is "checkbox" or "radio")
            {
                if (!kinds.All(kind => kind is "checkbox" or "radio"))
                {
                    Err($"{fid}: type {ftype} but claims non-button widgets ({Py(kinds.Order(StringComparer.Ordinal))})");
                }
            }
            else if (!kinds.All(kind => kind is "text" or "choice" or "signature"))
            {
                Err($"{fid}: type {ftype} but claims button widgets");
            }
            if (ftype == "radio")
            {
                var states = matched.SelectMany(name => States(pdfFields[name])).ToHashSet(StringComparer.Ordinal);
                var opts = (field["options"] as JsonArray ?? []).Select(option => Text(option?["value"]) ?? "").ToHashSet(StringComparer.Ordinal);
                if (!opts.SetEquals(states))
                {
                    Err($"{fid}: options {Py(opts.Order(StringComparer.Ordinal))} != PDF states {Py(states.Order(StringComparer.Ordinal))}");
                }
            }
            else if (ftype == "checkbox")
            {
                foreach (var name in matched)
                {
                    var states = States(pdfFields[name]).ToList();
                    if (states.Count > 1)
                    {
                        Err($"{fid}: {Repr(name)} has states {Py(states)}; model it as a radio");
                    }
                }
            }
            if (field.ContainsKey("options"))
            {
                if (ftype is not ("radio" or "checkbox"))
                {
                    Err($"{fid}: options only belong on radio/checkbox fields");
                }
                foreach (var option in field["options"] as JsonArray ?? [])
                {
                    if (option is not JsonObject optionObject || optionObject.Any(p => !OptionKeys.Contains(p.Key)))
                    {
                        Err($"{fid}: option has unknown keys: {Show(option)}");
                    }
                }
            }
        }

        var uncovered = pdfFields.Keys.Where(name => !claimed.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
        if (uncovered.Count > 0)
        {
            Err($"{uncovered.Count} official PDF fields not covered by any spec field: {Py(uncovered.Take(8))}{(uncovered.Count > 8 ? " …" : "")}");
        }
        if (pdfFields.Count == 0 && overlayBoxes.Count == 0 && fields.Count > 0)
        {
            Err("a flat form's fields must claim overlay boxes");
        }

        if (!sigPages.Distinct().Order().SequenceEqual(actualSigPages))
        {
            Err($"signature_pages {Py(sigPages)} != pages carrying a signature-type "
                + $"field {Py(actualSigPages)} — the forms engine's "
                + "signature-page selection (issue 13.11) trusts this list, so it "
                + "must exactly match every 'signature' field's own widget pages");
        }

        return new FormCheckResult(fields.Count, claimed.Count + overlayBoxes.Count, mapped, errors);
    }

    private static void CheckOverlay(
        string fid, string? ftype, JsonObject field, JsonObject pdf, bool formHasWidgets, long dumpPages,
        Dictionary<string, string> overlayBoxes, Action<string> err)
    {
        if (formHasWidgets)
        {
            err($"{fid}: overlay boxes are only for a form the PDF cannot fill; this one has widgets");
        }
        if (ftype == "radio")
        {
            err($"{fid}: a flat form has no export states — model the choice as checkbox boxes");
        }
        if (pdf["overlay"] is not JsonArray { Count: > 0 } boxes)
        {
            err($"{fid}: overlay must be a non-empty list of boxes");
            return;
        }
        foreach (var boxNode in boxes)
        {
            if (boxNode is not JsonObject box || !OverlayBoxRequiredKeys.All(box.ContainsKey))
            {
                err($"{fid}: overlay box needs name, page, x, y, w: {Show(boxNode)}");
                continue;
            }
            var unknown = box.Select(p => p.Key).Where(key => !OverlayBoxKeys.Contains(key)).Order(StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                err($"{fid}: overlay box has unknown keys: {Py(unknown)}");
            }
            if (Text(box["name"]) is not { Length: > 0 } name)
            {
                err($"{fid}: overlay box name must be a non-empty string");
                continue;
            }
            if (overlayBoxes.TryGetValue(name, out var other))
            {
                err($"overlay box {Repr(name)} claimed by both {other} and {fid}");
            }
            overlayBoxes[name] = fid;
            if (Integer(box["page"]) is not { } page || page < 1 || page > dumpPages)
            {
                err($"{fid}: overlay box {Repr(name)} page {ShowRepr(box["page"])} is not a page of the PDF");
            }
            foreach (var key in new[] { "x", "y", "w", "h", "size" })
            {
                if (box.ContainsKey(key) && Number(box[key]) is not >= 0)
                {
                    err($"{fid}: overlay box {Repr(name)} {key} must be a non-negative number");
                }
            }
            if ((ftype == "checkbox") != box.ContainsKey("h"))
            {
                err($"{fid}: overlay box {Repr(name)} carries h only when the field is a checkbox");
            }
            if (box.ContainsKey("size") && (ftype == "checkbox" || !Truthy(box["size"])))
            {
                err($"{fid}: overlay box {Repr(name)} size is a positive point size, and only on a text box");
            }
        }
        if (field.ContainsKey("options"))
        {
            err($"{fid}: options only belong on radio/checkbox widget fields");
        }
    }

    private static IEnumerable<string> States(JsonObject pdfField) =>
        (pdfField["states"] as JsonArray ?? []).Select(state => Text(state) ?? "");

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>A JSON integer (Python's int): 1 is one, 1.0 and true are not.</summary>
    private static long? Integer(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number : null;

    /// <summary>A JSON number (Python's int or float, never bool).</summary>
    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? AsDouble(value) : null;

    // Through the text, not GetValue<double>: a node built from an int (as a
    // test builds one) refuses to be read back as a double.
    private static double AsDouble(JsonValue value) => double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Python truthiness for a JSON value.</summary>
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => AsDouble(value) != 0,
            JsonValueKind.True => true,
            _ => false,
        },
        _ => false,
    };

    private static string Repr(string text) => $"'{text}'";

    private static string Show(JsonNode? node) => node is null ? "None" : Text(node) is { } text ? text : node.ToJsonString();

    private static string ShowRepr(JsonNode? node) => Text(node) is { } text ? Repr(text) : Show(node);

    private static string Py<T>(IEnumerable<T> items) =>
        "[" + string.Join(", ", items.Select(item => item is string text ? Repr(text) : Convert.ToString(item, CultureInfo.InvariantCulture))) + "]";
}

/// <summary>
/// One spec's result: its spec fields, the PDF fields (or overlay boxes) they
/// cover, how many map to an entity, and every error found.
/// </summary>
internal sealed record FormCheckResult(int SpecFields, int Covered, int EntityMapped, IReadOnlyList<string> Errors);
