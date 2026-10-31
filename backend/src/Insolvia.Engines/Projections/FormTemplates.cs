using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Engines.Regulatory;

namespace Insolvia.Engines.Projections;

// The form template registry's field half (from core/form_templates.py): each
// release's spec.json (the curated logical fields, their types and exact radio
// export states) and acroform.json (the machine-extracted widget dump), loaded
// and validated by the rules of the loader it was ported from. The template PDF itself, and
// the check that it hashes to the manifest's sha256, belong to the fill engine
// (Insolvia.Forms, 18.16), which ships the PDFs; a projection never needs the
// bytes, only which fields exist and which widget states they take.

/// <summary>One AcroForm field of the official PDF, as the dump recorded it.</summary>
public sealed record Widget(string Name, string Kind, IReadOnlyList<int> Pages, IReadOnlyList<string> States, IReadOnlyList<string> ChoiceOptions, int? MaxLen);

/// <summary>One drawing position on a FLAT release (a PDF with no AcroForm): points from the page's bottom-left.</summary>
public sealed record OverlayBox(string Name, int Page, double X, double Y, double W, double? H = null, double? Size = null)
{
    /// <summary>A checkbox box (an X drawn across its square) rather than a text baseline.</summary>
    public bool IsCheck => H is not null;
}

/// <summary>One radio option: the PDF's exact export state and the canonical value it maps from.</summary>
public sealed record OptionSpec(string Value, string? MapsToValue = null);

/// <summary>One logical field of the form, its widget (or overlay box) claims in printed row order.</summary>
public sealed record FieldSpec(string Id, string Type, string Label, IReadOnlyList<string> PdfNames, int? Part, string? Line, IReadOnlyList<OptionSpec> Options, string Notes);

/// <summary>One immutable release of a form series.</summary>
public sealed record FormRelease(
    string SeriesId,
    DateOnly EffectiveDate,
    int Sequence,
    string SourceUrl,
    string SourceSha256,
    string Notes,
    string Form,
    string OfficialNumber,
    string Title,
    string Revision,
    IReadOnlyList<FieldSpec> Fields,
    IReadOnlyDictionary<string, Widget> Widgets,
    IReadOnlyDictionary<string, OverlayBox> Boxes,
    IReadOnlyList<int> SignaturePages) : IRelease
{
    /// <summary>No AcroForm: the engine draws overlay boxes instead of setting widgets.</summary>
    public bool IsFlat => Widgets.Count == 0;

    /// <summary><c>series@date[+sequence]</c>.</summary>
    public string ReleaseId => Releases.Id(SeriesId, EffectiveDate, Sequence);

    /// <summary>What <c>case.form_revisions</c> stores for this series.</summary>
    public string Pin => Releases.Pin(EffectiveDate, Sequence);

    /// <summary>The field with this id; <see cref="EngineKeyException"/> when there is none.</summary>
    public FieldSpec Field(string fieldId) =>
        Fields.FirstOrDefault(field => field.Id == fieldId)
        ?? throw new EngineKeyException($"{ReleaseId} has no field {Exact.Repr(fieldId)}");
}

/// <summary>The form registry: load, validate, resolve (Python's <c>core/form_templates.py</c>, less the PDF bytes).</summary>
public static partial class FormTemplates
{
    /// <summary>The closed set of field types a spec may declare.</summary>
    public static readonly IReadOnlySet<string> FieldTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "text", "money", "integer", "date", "year", "state", "district", "ssn_itin", "ein", "phone", "email", "signature", "checkbox", "radio",
    };

    private static readonly HashSet<string> ButtonKinds = new(StringComparer.Ordinal) { "checkbox", "radio" };
    private static readonly HashSet<string> EntryKinds = new(StringComparer.Ordinal) { "text", "choice", "signature" };

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<FormRelease>>> Shipped = new(() => LoadRegistry(RegistryTree.Shipped));

    /// <summary>The committed registry.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<FormRelease>> Registry => Shipped.Value;

    /// <summary>Loads and validates every form release under a registry root.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<FormRelease>> LoadRegistry(RegistryTree root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var series = new SortedDictionary<string, IReadOnlyList<FormRelease>>(StringComparer.Ordinal);
        foreach (var name in root.ChildDirectories("form"))
        {
            var seriesId = $"form/{name}";
            var loaded = root.ChildDirectories($"form/{name}")
                .Select(release => LoadRelease(root, $"form/{name}/{release}", release, seriesId))
                .ToList();
            if (loaded.Count == 0)
            {
                throw Fail(seriesId, "series has no releases");
            }

            series[seriesId] = OrderedReleases(loaded, seriesId);
        }

        if (series.Count == 0)
        {
            throw new MalformedReleaseException("the form template registry is empty");
        }

        return series;
    }

    /// <summary>The series ids, sorted.</summary>
    public static IReadOnlyList<string> SeriesIds() => [.. Registry.Keys.Order(StringComparer.Ordinal)];

    /// <summary>A series' releases, oldest first.</summary>
    public static IReadOnlyList<FormRelease> ReleasesOf(string seriesId) =>
        Registry.TryGetValue(seriesId, out var found) ? found : throw new EngineKeyException($"unknown series {Exact.Repr(seriesId)}");

    /// <summary>The revision in force on <paramref name="asOf"/>.</summary>
    public static FormRelease Resolve(string seriesId, DateOnly asOf) =>
        Releases.Pick(ReleasesOf(seriesId), asOf, "refusing to render a form revision that does not describe that date");

    /// <summary>That exact release.</summary>
    public static FormRelease Get(string seriesId, string releaseId) =>
        ReleasesOf(seriesId).FirstOrDefault(release => release.ReleaseId == releaseId)
        ?? throw new EngineKeyException($"series {Exact.Repr(seriesId)} has no release {Exact.Repr(releaseId)}");

    /// <summary>Newest by (effective date, sequence), even if still in the future.</summary>
    public static FormRelease Latest(string seriesId) => ReleasesOf(seriesId)[^1];

    /// <summary>The pin map packet assembly records on the case: every series resolved as of the date.</summary>
    public static IReadOnlyDictionary<string, string> RevisionsAsOf(DateOnly asOf) =>
        SeriesIds().ToDictionary(id => id, id => Resolve(id, asOf).Pin, StringComparer.Ordinal);

    [GeneratedRegex(@"^(\d+)\.(\d+)\b")]
    private static partial Regex RowMarker();

    [GeneratedRegex(@"^(.*?)_(\d+)$")]
    private static partial Regex RowSuffix();

    private static MalformedReleaseException Fail(string where, string problem) => new($"malformed form release {where}: {problem}");

    private static string StrField(JsonObject data, string key, string where) => ReleaseParsing.StrField(data, key, where, Fail);

    private static List<FormRelease> OrderedReleases(List<FormRelease> loaded, string seriesId)
    {
        var ordered = loaded.OrderBy(release => release.EffectiveDate).ThenBy(release => release.Sequence).ToList();
        if (ordered.Select(release => release.ReleaseId).Distinct(StringComparer.Ordinal).Count() != ordered.Count)
        {
            throw Fail(seriesId, "duplicate release ids");
        }

        return ordered;
    }

    private static JsonObject LoadJson(RegistryTree root, string path, string where)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var text = root.Read(path) ?? throw Fail(where, $"{name}: [Errno 2] No such file or directory: {Exact.Repr(name)}");
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException error)
        {
            throw Fail(where, $"{name}: {error.Message}");
        }

        return parsed as JsonObject ?? throw Fail(where, $"{name} is not a JSON object");
    }

    private static List<int> IntList(JsonNode? value, Func<string> problem)
    {
        if (value is not JsonArray list)
        {
            throw new MalformedReleaseException(problem());
        }

        var numbers = new List<int>();
        foreach (var item in list)
        {
            if (!Core.Json.TryGetWholeNumber(item, out var number))
            {
                throw new MalformedReleaseException(problem());
            }

            numbers.Add((int)number);
        }

        return numbers;
    }

    private static List<string> StringList(JsonNode? value, Func<string> problem)
    {
        if (value is not JsonArray list || !list.All(item => Core.Json.AsString(item) is not null))
        {
            throw new MalformedReleaseException(problem());
        }

        return [.. list.Select(item => Core.Json.AsString(item)!)];
    }

    private static Dictionary<string, Widget> Widgets(JsonObject dump, string where)
    {
        if (dump["fields"] is not JsonArray rawFields)
        {
            throw Fail(where, "acroform.json fields missing");
        }

        var widgets = new Dictionary<string, Widget>(StringComparer.Ordinal);
        foreach (var node in rawFields)
        {
            if (node is not JsonObject raw)
            {
                throw Fail(where, "acroform field is not an object");
            }

            var name = StrField(raw, "name", where);
            if (widgets.ContainsKey(name))
            {
                throw Fail(where, $"duplicate acroform field name {Exact.Repr(name)}");
            }

            var kind = StrField(raw, "kind", where);
            var pages = IntList(raw.TryGetPropertyValue("pages", out var p) ? p : new JsonArray(), () => Fail(where, $"{Exact.Repr(name)}: pages must be a list of integers").Message);
            var states = StringList(raw.TryGetPropertyValue("states", out var s) ? s : new JsonArray(), () => Fail(where, $"{Exact.Repr(name)}: states must be a list of strings").Message);
            var optionsNode = raw["options"];
            var choiceOptions = optionsNode is null or JsonArray { Count: 0 }
                ? []
                : StringList(optionsNode, () => Fail(where, $"{Exact.Repr(name)}: options must be a list of strings").Message);
            int? maxLen = null;
            if (raw["max_len"] is { } maxLenNode)
            {
                if (!Core.Json.TryGetWholeNumber(maxLenNode, out var number) || number < 1)
                {
                    throw Fail(where, $"{Exact.Repr(name)}: max_len must be a positive integer");
                }

                maxLen = (int)number;
            }

            widgets[name] = new Widget(name, kind, pages, states, choiceOptions, maxLen);
        }

        return widgets;
    }

    private static List<string>? SuffixOrder(List<string> hits)
    {
        var parsed = new List<(int Number, string Name)>();
        var stems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in hits)
        {
            var match = RowSuffix().Match(name);
            if (match.Success)
            {
                stems.Add(match.Groups[1].Value);
                parsed.Add((int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), name));
            }
            else
            {
                stems.Add(name);
                parsed.Add((1, name));
            }
        }

        var numbers = parsed.Select(item => item.Number).ToList();
        if (stems.Count == 1 && numbers.Distinct().Count() == numbers.Count)
        {
            return [.. parsed.OrderBy(item => item.Number).ThenBy(item => item.Name, StringComparer.Ordinal).Select(item => item.Name)];
        }

        return null;
    }

    private static List<string> PatternOrder(List<string> hits, Dictionary<string, int> dumpOrder)
    {
        if (hits.All(name => RowMarker().IsMatch(name)))
        {
            return [.. hits
                .Select(name => (Name: name, Match: RowMarker().Match(name)))
                .OrderBy(item => long.Parse(item.Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .ThenBy(item => long.Parse(item.Match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
                .Select(item => item.Name)];
        }

        return SuffixOrder(hits) ?? [.. hits.OrderBy(name => dumpOrder[name])];
    }

    private static List<OptionSpec> Options(JsonNode? raw, string where, string fieldId)
    {
        if (raw is null)
        {
            return [];
        }

        if (raw is not JsonArray list)
        {
            throw Fail(where, $"{fieldId}: options must be a list");
        }

        var parsed = new List<OptionSpec>();
        foreach (var node in list)
        {
            if (node is not JsonObject item)
            {
                throw Fail(where, $"{fieldId}: option is not an object");
            }

            var mapsTo = item["maps_to_value"];
            if (mapsTo is not null && Core.Json.AsString(mapsTo) is null)
            {
                throw Fail(where, $"{fieldId}: maps_to_value must be a string");
            }

            parsed.Add(new OptionSpec(StrField(item, "value", where), Core.Json.AsString(mapsTo)));
        }

        return parsed;
    }

    private static double Number(JsonNode? value, string where, string what)
    {
        if (!Core.Json.TryGetNumber(value, out var number) || number < 0)
        {
            throw Fail(where, $"{what} must be a non-negative number");
        }

        return number;
    }

    private static List<string> OverlayBoxes(JsonNode? raw, string where, string fieldId, string fieldType, int pageCount, Dictionary<string, OverlayBox> boxes)
    {
        if (raw is not JsonArray list || list.Count == 0)
        {
            throw Fail(where, $"{fieldId}: overlay must be a non-empty list of boxes");
        }

        if (fieldType == "radio")
        {
            throw Fail(where, $"{fieldId}: a flat form has no export states for a radio");
        }

        var names = new List<string>();
        foreach (var node in list)
        {
            if (node is not JsonObject item)
            {
                throw Fail(where, $"{fieldId}: overlay box is not an object");
            }

            var name = StrField(item, "name", where);
            if (boxes.ContainsKey(name))
            {
                throw Fail(where, $"overlay box {Exact.Repr(name)} claimed twice");
            }

            if (!Core.Json.TryGetWholeNumber(item["page"], out var page) || page < 1 || page > pageCount)
            {
                throw Fail(where, $"{fieldId}: overlay box {Exact.Repr(name)} page is not on the PDF");
            }

            var height = item["h"];
            if ((fieldType == "checkbox") != (height is not null))
            {
                throw Fail(where, $"{fieldId}: overlay box {Exact.Repr(name)} carries h exactly when the field is a checkbox");
            }

            var size = item["size"];
            if (size is not null && height is not null)
            {
                throw Fail(where, $"{fieldId}: overlay box {Exact.Repr(name)} is a checkbox and has no size");
            }

            boxes[name] = new OverlayBox(
                name,
                (int)page,
                Number(item["x"], where, $"{fieldId}: overlay box {Exact.Repr(name)} x"),
                Number(item["y"], where, $"{fieldId}: overlay box {Exact.Repr(name)} y"),
                Number(item["w"], where, $"{fieldId}: overlay box {Exact.Repr(name)} w"),
                height is not null ? Number(height, where, $"{fieldId}: overlay box {Exact.Repr(name)} h") : null,
                size is not null ? Number(size, where, $"{fieldId}: overlay box {Exact.Repr(name)} size") : null);
            names.Add(name);
        }

        return names;
    }

    private static FieldSpec ParseField(JsonNode? node, string where, Dictionary<string, Widget> widgets, Dictionary<string, int> dumpOrder, Dictionary<string, OverlayBox> boxes, int pageCount)
    {
        if (node is not JsonObject raw)
        {
            throw Fail(where, "spec field is not an object");
        }

        var fieldId = StrField(raw, "id", where);
        var fieldType = StrField(raw, "type", where);
        if (!FieldTypes.Contains(fieldType))
        {
            throw Fail(where, $"{fieldId}: unknown type {Exact.Repr(fieldType)}");
        }

        int? part = null;
        if (raw["part"] is { } partNode)
        {
            part = Core.Json.TryGetWholeNumber(partNode, out var partNumber) ? (int)partNumber : throw Fail(where, $"{fieldId}: part must be an integer or null");
        }

        var lineNode = raw["line"];
        var line = lineNode is null ? null : Core.Json.AsString(lineNode) ?? throw Fail(where, $"{fieldId}: line must be a string or null");
        var notes = raw.TryGetPropertyValue("notes", out var notesNode)
            ? Core.Json.AsString(notesNode) ?? throw Fail(where, $"{fieldId}: notes must be a string")
            : string.Empty;
        var label = StrField(raw, "label", where);

        if (raw["pdf"] is not JsonObject pdf || pdf.Count == 0)
        {
            throw Fail(where, $"{fieldId}: pdf claim block missing");
        }

        if (pdf.ContainsKey("overlay"))
        {
            if (widgets.Count > 0)
            {
                throw Fail(where, $"{fieldId}: overlay boxes on a form that has widgets");
            }

            if (pdf.Count != 1)
            {
                throw Fail(where, $"{fieldId}: overlay cannot be mixed with names/pattern");
            }

            return new FieldSpec(fieldId, fieldType, label, OverlayBoxes(pdf["overlay"], where, fieldId, fieldType, pageCount, boxes), part, line, [], notes);
        }

        var claimed = new List<string>();
        var namesNode = pdf.TryGetPropertyValue("names", out var n) ? n : new JsonArray();
        if (namesNode is not JsonArray names)
        {
            throw Fail(where, $"{fieldId}: pdf.names must be a list");
        }

        foreach (var nameNode in names)
        {
            var name = Core.Json.AsString(nameNode);
            if (name is null || !widgets.ContainsKey(name))
            {
                throw Fail(where, $"{fieldId}: claims unknown PDF field {Exact.Repr(nameNode)}");
            }

            claimed.Add(name);
        }

        if (pdf["pattern"] is { } patternNode)
        {
            var pattern = Core.Json.AsString(patternNode) ?? throw Fail(where, $"{fieldId}: pdf.pattern must be a string");
            var regex = new Regex($"\\A(?:{pattern})\\z", RegexOptions.CultureInvariant);
            var hits = widgets.Keys.Where(name => regex.IsMatch(name)).ToList();
            if (hits.Count == 0)
            {
                throw Fail(where, $"{fieldId}: pattern matched nothing: {Exact.Repr(pattern)}");
            }

            claimed.AddRange(PatternOrder(hits, dumpOrder));
        }

        var ordered = claimed.Distinct(StringComparer.Ordinal).ToList();
        if (ordered.Count != claimed.Count)
        {
            throw Fail(where, $"{fieldId}: claims the same PDF field twice");
        }

        var kinds = ordered.Select(name => widgets[name].Kind).ToHashSet(StringComparer.Ordinal);
        var allowed = ButtonKinds.Contains(fieldType) ? ButtonKinds : EntryKinds;
        if (!kinds.IsSubsetOf(allowed))
        {
            throw Fail(where, $"{fieldId}: type {fieldType} claims widget kinds [{string.Join(", ", kinds.Order(StringComparer.Ordinal).Select(Exact.Repr))}]");
        }

        var options = Options(raw["options"], where, fieldId);
        if (fieldType == "radio")
        {
            var states = ordered.SelectMany(name => widgets[name].States).ToHashSet(StringComparer.Ordinal);
            var declared = options.Select(option => option.Value).ToHashSet(StringComparer.Ordinal);
            if (!declared.SetEquals(states))
            {
                throw Fail(where, $"{fieldId}: options [{string.Join(", ", declared.Order(StringComparer.Ordinal).Select(Exact.Repr))}] != PDF export states [{string.Join(", ", states.Order(StringComparer.Ordinal).Select(Exact.Repr))}]");
            }
        }

        return new FieldSpec(fieldId, fieldType, label, ordered, part, line, options, notes);
    }

    private static FormRelease LoadRelease(RegistryTree root, string directory, string name, string seriesId)
    {
        var where = $"{seriesId}/{name}";
        var (effective, sequence) = ReleaseParsing.ParseDirectoryName(name, where, Fail);
        var manifest = LoadJson(root, $"{directory}/manifest.json", where);
        var source = ReleaseParsing.ManifestSource(manifest, seriesId, effective, sequence, where, Fail);
        var sourceSha256 = StrField(source, "sha256", where);
        var manifestNotes = Core.Json.AsString(manifest["notes"]);
        if (manifestNotes is null || manifestNotes.Trim().Length == 0)
        {
            throw Fail(where, "manifest notes missing — say what this release is");
        }

        var dump = LoadJson(root, $"{directory}/acroform.json", where);
        if (dump["source"] is not JsonObject dumpSource || Core.Json.AsString(dumpSource["pdf_sha256"]) != sourceSha256)
        {
            throw Fail(where, "acroform.json source.pdf_sha256 disagrees with the manifest");
        }

        var widgets = Widgets(dump, where);
        var dumpOrder = widgets.Keys.Select((key, index) => (key, index)).ToDictionary(item => item.key, item => item.index, StringComparer.Ordinal);
        if (!Core.Json.TryGetWholeNumber(dump["pages"], out var pageCount) || pageCount < 1)
        {
            throw Fail(where, "acroform.json pages missing");
        }

        var spec = LoadJson(root, $"{directory}/spec.json", where);
        var form = StrField(spec, "form", where);
        if (form != seriesId["form/".Length..])
        {
            throw Fail(where, $"spec form {Exact.Repr(form)} disagrees with the series id");
        }

        if (Core.Json.AsString(spec["form"]) != Core.Json.AsString(dump["form"]))
        {
            throw Fail(where, "spec form disagrees with acroform form");
        }

        var revision = StrField(spec, "revision", where);
        if (revision != Core.Json.AsString(dump["revision"]))
        {
            throw Fail(where, "spec revision disagrees with acroform revision");
        }

        foreach (var (doc, docName) in new[] { (spec, "spec"), (dump, "acroform") })
        {
            if (Core.Json.AsString(doc["effective_date"]) != Exact.Iso(effective))
            {
                throw Fail(where, $"{docName} effective_date disagrees with the path");
            }
        }

        if (spec["fields"] is not JsonArray rawFields)
        {
            throw Fail(where, "spec fields missing");
        }

        if (rawFields.Count == 0 && widgets.Count > 0)
        {
            throw Fail(where, "spec fields empty on a form that has widgets");
        }

        var fields = new List<FieldSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var claimedBy = new Dictionary<string, string>(StringComparer.Ordinal);
        var boxes = new Dictionary<string, OverlayBox>(StringComparer.Ordinal);
        foreach (var raw in rawFields)
        {
            var parsed = ParseField(raw, where, widgets, dumpOrder, boxes, (int)pageCount);
            if (!seen.Add(parsed.Id))
            {
                throw Fail(where, $"duplicate field id {parsed.Id}");
            }

            foreach (var pdfName in parsed.PdfNames)
            {
                if (claimedBy.TryGetValue(pdfName, out var owner))
                {
                    throw Fail(where, $"PDF field {Exact.Repr(pdfName)} claimed by both {owner} and {parsed.Id}");
                }

                claimedBy[pdfName] = parsed.Id;
            }

            fields.Add(parsed);
        }

        var uncovered = widgets.Where(pair => pair.Value.Kind != "pushbutton" && !claimedBy.ContainsKey(pair.Key))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (uncovered.Count > 0)
        {
            throw Fail(where, $"{uncovered.Count} fillable PDF fields not claimed by any spec field: [{string.Join(", ", uncovered.Take(5).Select(Exact.Repr))}]");
        }

        var signaturePages = IntList(spec["signature_pages"], () => Fail(where, "signature_pages must be a list of positive integers").Message);
        if (signaturePages.Any(page => page <= 0))
        {
            throw Fail(where, "signature_pages must be a list of positive integers");
        }

        if (!signaturePages.SequenceEqual(signaturePages.Distinct().Order()))
        {
            throw Fail(where, "signature_pages must be sorted, strictly increasing");
        }

        var actualSignaturePages = fields
            .Where(field => field.Type == "signature")
            .SelectMany(field => field.PdfNames)
            .Where(widgets.ContainsKey)
            .SelectMany(pdfName => widgets[pdfName].Pages)
            .ToHashSet();
        if (!actualSignaturePages.SetEquals(signaturePages))
        {
            throw Fail(where, $"signature_pages [{string.Join(", ", signaturePages)}] != pages carrying a signature-type field's widget [{string.Join(", ", actualSignaturePages.Order())}]");
        }

        return new FormRelease(
            SeriesId: seriesId,
            EffectiveDate: effective,
            Sequence: sequence,
            SourceUrl: StrField(source, "url", where),
            SourceSha256: sourceSha256,
            Notes: manifestNotes,
            Form: form,
            OfficialNumber: StrField(spec, "official_number", where),
            Title: StrField(spec, "title", where),
            Revision: revision,
            Fields: fields,
            Widgets: widgets,
            Boxes: boxes,
            SignaturePages: signaturePages);
    }
}
