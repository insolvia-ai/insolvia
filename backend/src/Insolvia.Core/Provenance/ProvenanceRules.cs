using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Insolvia.Core.Provenance;

/// <summary>
/// Where every stored value came from, and the two rules that make it
/// enforceable (docs/reference/case-data-model.md; ported from
/// <c>provenance.py</c>):
/// <list type="number">
/// <item>Every populated field carries a provenance entry — otherwise rule 2
/// is evaded by omitting the key (<see cref="RequireProvenance"/>).</item>
/// <item>A field whose source needs confirmation and has no
/// <c>confirmed_at</c>/<c>confirmed_by</c> cannot exist on a case record —
/// the write is rejected (<see cref="Parse"/>).</item>
/// </list>
/// It does NOT check completeness: a half-finished intake must persist.
/// </summary>
public static partial class ProvenanceRules
{
    /// <summary>Sources whose value is machine-supplied.</summary>
    public static readonly IReadOnlySet<ProvenanceSource> MachineSources =
        new HashSet<ProvenanceSource> { ProvenanceSource.AiExtracted, ProvenanceSource.Imported };

    /// <summary>Every source invariant 2 covers: the machine-supplied ones and a debtor's answer.</summary>
    public static readonly IReadOnlySet<ProvenanceSource> ConfirmationRequired =
        new HashSet<ProvenanceSource> { ProvenanceSource.AiExtracted, ProvenanceSource.Imported, ProvenanceSource.ClientAnswered };

    /// <summary>
    /// Sources the SERVER mints and a whole-record save may only KEEP:
    /// <c>client</c> (only the copy routes write it) and <c>client_answered</c>
    /// (only the review queue writes it).
    /// </summary>
    public static readonly IReadOnlySet<ProvenanceSource> KeepOnlySources =
        new HashSet<ProvenanceSource> { ProvenanceSource.Client, ProvenanceSource.ClientAnswered };

    // Stamped by the server, never supplied: matched as WHOLE paths, never by
    // first segment (a subtree parked under `created_at` must not be exempt).
    private static readonly HashSet<string> ServerOwned =
        new(StringComparer.Ordinal) { "id", "case_id", "created_at", "updated_at", "client_id" };

    private static readonly Dictionary<ProvenanceSource, string> KeepOnlyMessages = new()
    {
        [ProvenanceSource.Client] = "Only a value copied from the client, and unchanged since, can say it came from the client.",
        [ProvenanceSource.ClientAnswered] = "Only a value accepted from the client's own answer, and unchanged since, can say the client answered it.",
    };

    /// <summary>
    /// The ids a list element may carry and still be addressed by a path
    /// (<c>other_names_used[n1].surname</c>). Exported because entity parsers
    /// take list-element ids FROM THE CLIENT and must refuse one this rejects.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    public static partial Regex AddressableId();

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex FieldName();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(?:\[[A-Za-z0-9_-]+\])?(?:\.[a-z][a-z0-9_]*(?:\[[A-Za-z0-9_-]+\])?)*$")]
    private static partial Regex FieldPath();

    [GeneratedRegex(@"^([a-z][a-z0-9_]*)(?:\[([A-Za-z0-9_-]+)\])?$")]
    private static partial Regex PathSegment();

    /// <summary>
    /// Validate the <c>provenance</c> map of a record (Python's
    /// <c>parse_provenance</c>). Every key must be a well-formed field path —
    /// a typo'd path would otherwise be stored for a field nobody reads.
    /// Raises <see cref="FieldValidationException"/> on any bad entry.
    /// </summary>
    public static ValueDictionary<string, ProvenanceEntry> Parse(JsonNode? value)
    {
        if (value is null)
        {
            return ValueCollections.EmptyDictionary<string, ProvenanceEntry>();
        }

        if (value is not JsonObject map)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["provenance"] = "Provenance must be an object." });
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = new List<KeyValuePair<string, ProvenanceEntry>>();
        foreach (var (path, raw) in map)
        {
            if (!FieldPath().IsMatch(path))
            {
                errors[$"provenance.{path}"] = "Not a field path.";
                continue;
            }

            var entry = ParseEntry(path, raw, errors);
            if (entry is not null)
            {
                entries.Add(new(path, entry));
            }
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return entries.ToValueDictionary();
    }

    /// <summary>
    /// Every field path in <paramref name="record"/> that actually holds a
    /// value (Python's <c>populated_paths</c>). <c>null</c>, and — inside a
    /// map — an empty string, list or map are absent; <c>false</c> and
    /// <c>0</c> are PRESENT (they are answers). List elements are addressed by
    /// their own <c>id</c>; a list whose elements cannot all be addressed is
    /// attributed whole. Every emitted path is one <see cref="Parse"/>
    /// accepts — a key that is not a legal field name raises here instead.
    /// </summary>
    public static List<string> PopulatedPaths(JsonNode? record, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var paths = new List<string>();
        switch (record)
        {
            case null:
                return paths;

            case JsonObject map:
                foreach (var (key, value) in map)
                {
                    if (key == "id" && prefix.EndsWith(']'))
                    {
                        continue;
                    }

                    if (!FieldName().IsMatch(key))
                    {
                        var where = prefix.Length > 0 ? $"provenance.{prefix}.{key}" : $"provenance.{key}";
                        throw new FieldValidationException(new Dictionary<string, string>
                        {
                            [where] = "Not a field name, so nothing can record where it came from.",
                        });
                    }

                    paths.AddRange(PopulatedPaths(value, prefix.Length > 0 ? $"{prefix}.{key}" : key));
                }

                return paths;

            case JsonArray list:
                // Decided BEFORE walking: one unaddressable element makes the
                // whole list one path, whatever order the elements are in.
                var ids = list.Select(element => Json.AsString(Json.Get(element, "id"))).ToList();
                var addressable = ids.All(id => id is not null && AddressableId().IsMatch(id));
                if (!addressable)
                {
                    if (prefix.Length > 0)
                    {
                        paths.Add(prefix);
                    }

                    return paths;
                }

                for (var index = 0; index < list.Count; index++)
                {
                    paths.AddRange(PopulatedPaths(list[index], $"{prefix}[{ids[index]}]"));
                }

                return paths;

            default:
                var text = Json.AsString(record);
                if (text is not null)
                {
                    if (text.Length > 0 && prefix.Length > 0)
                    {
                        paths.Add(prefix);
                    }

                    return paths;
                }

                if (prefix.Length > 0)
                {
                    paths.Add(prefix);
                }

                return paths;
        }
    }

    /// <summary>
    /// The value <paramref name="path"/> addresses in <paramref name="record"/>
    /// — the inverse of <see cref="PopulatedPaths"/>; a list element is found
    /// by its <c>id</c>, never by position. Null when nothing is there.
    /// </summary>
    public static JsonNode? ValueAt(JsonNode? record, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var current = record;
        foreach (var segment in path.Split('.'))
        {
            var match = PathSegment().Match(segment);
            if (!match.Success || current is not JsonObject map)
            {
                return null;
            }

            current = map[match.Groups[1].Value];
            if (match.Groups[2].Success)
            {
                var elementId = match.Groups[2].Value;
                if (current is not JsonArray list)
                {
                    return null;
                }

                current = list.FirstOrDefault(element => element is JsonObject && Json.AsString(Json.Get(element, "id")) == elementId);
            }
        }

        return current;
    }

    /// <summary>
    /// INVARIANT 1: every populated field carries a provenance entry. Checked
    /// against the record as submitted, so it cannot be satisfied by
    /// provenance for absent fields nor evaded by omitting the key. The
    /// server-stamped scalars are exempt by WHOLE path.
    /// </summary>
    public static void RequireProvenance(JsonObject record, IReadOnlyDictionary<string, ProvenanceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in PopulatedPaths(record))
        {
            if (!entries.ContainsKey(path) && !ServerOwned.Contains(path))
            {
                errors[$"provenance.{path}"] = "This field has a value but no provenance.";
            }
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }
    }

    /// <summary>
    /// A save may KEEP a <see cref="KeepOnlySources"/> entry, never mint one:
    /// legitimate only as the entry the stored record already carries at that
    /// path, on the value that path already holds. Entries at paths
    /// <paramref name="body"/> leaves empty are not checked; a null
    /// <paramref name="storedBody"/> is a create, where nothing can be kept.
    /// </summary>
    public static void RequireServerSourcesKept(
        JsonObject body,
        IReadOnlyDictionary<string, ProvenanceEntry> entries,
        JsonObject? storedBody,
        IReadOnlyDictionary<string, ProvenanceEntry>? storedEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var populated = PopulatedPaths(body).ToHashSet(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, entry) in entries)
        {
            if (!KeepOnlySources.Contains(entry.Source) || !populated.Contains(path))
            {
                continue;
            }

            ProvenanceEntry? kept = null;
            storedEntries?.TryGetValue(path, out kept);
            if (storedBody is null
                || kept != entry
                || !Json.ValueEquals(ValueAt(storedBody, path), ValueAt(body, path)))
            {
                errors[$"provenance.{path}"] = KeepOnlyMessages[entry.Source];
            }
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }
    }

    /// <summary>
    /// The map as stored and as sent (Python's <c>provenance_json</c>):
    /// snake_case members, and null members DROPPED rather than written — a
    /// <c>staff_typed</c> entry is three quarters empty and rides on every record.
    /// </summary>
    public static JsonObject ToJson(IReadOnlyDictionary<string, ProvenanceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var json = new JsonObject();
        foreach (var (path, entry) in entries)
        {
            var member = new JsonObject { ["source"] = entry.Source.ToWire() };
            AddIfPresent(member, "confirmed_by", entry.ConfirmedBy);
            AddIfPresent(member, "confirmed_at", entry.ConfirmedAt);
            AddIfPresent(member, "document_id", entry.DocumentId);
            if (entry.Locator is not null)
            {
                member["locator"] = entry.Locator.DeepClone();
            }

            AddIfPresent(member, "extraction_id", entry.ExtractionId);
            if (entry.Confidence is { } confidence)
            {
                member["confidence"] = confidence;
            }

            AddIfPresent(member, "library_creditor_id", entry.LibraryCreditorId);
            AddIfPresent(member, "client_id", entry.ClientId);
            AddIfPresent(member, "copied_from_case_id", entry.CopiedFromCaseId);
            json[path] = member;
        }

        return json;
    }

    private static void AddIfPresent(JsonObject target, string key, string? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    // A real instant, PARSED rather than pattern-matched: `0000-00-00T…Z`
    // matches a regex of the right shape and is not a moment. The shape check
    // narrows to the extended ISO forms Python's `datetime.fromisoformat`
    // accepted (date, `T` or space, hours with optional minutes, seconds and
    // fraction); the parse is what decides the instant exists.
    private static bool IsUtcTimestamp(JsonNode? value)
    {
        var text = Json.AsString(value);
        if (text is null || !UtcTimestampShape().IsMatch(text))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}(?::\d{2}(?::\d{2}(?:\.\d+)?)?)?Z\z")]
    private static partial Regex UtcTimestampShape();

    private static bool CheckLocator(JsonNode? value, string path, Dictionary<string, string> errors)
    {
        if (value is not JsonObject locator)
        {
            errors[$"{path}.locator"] = "locator must be an object.";
            return false;
        }

        var documentId = locator["document_id"];
        if (documentId is not null && !Json.IsString(documentId))
        {
            errors[$"{path}.locator.document_id"] = "document_id must be a string.";
            return false;
        }

        var page = locator["page"];
        if (page is not null && (!Json.TryGetWholeNumber(page, out var number) || number < 1))
        {
            // 1-based, as the spec says and as a reader counts pages.
            errors[$"{path}.locator.page"] = "page must be a whole number, 1 or more.";
            return false;
        }

        var region = locator["region"];
        if (region is null)
        {
            return true;
        }

        if (region is not JsonObject box)
        {
            errors[$"{path}.locator.region"] = "region must be an object.";
            return false;
        }

        foreach (var member in new[] { "x", "y", "width", "height" })
        {
            var size = box[member];
            if (size is null)
            {
                continue;
            }

            if (!Json.TryGetNumber(size, out var fraction) || !double.IsFinite(fraction) || fraction < 0.0 || fraction > 1.0)
            {
                errors[$"{path}.locator.region.{member}"] = "Must be a fraction of the page between 0 and 1.";
                return false;
            }
        }

        return true;
    }

    private static ProvenanceEntry? ParseEntry(string path, JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is not JsonObject raw)
        {
            errors[$"provenance.{path}"] = "Provenance must be an object.";
            return null;
        }

        if (!Wire.TryParse<ProvenanceSource>(Json.AsString(raw["source"]), out var source))
        {
            errors[$"provenance.{path}.source"] = "Source must be one of " + string.Join(", ", Wire.Names<ProvenanceSource>()) + ".";
            return null;
        }

        var confirmedAtNode = raw["confirmed_at"];
        if (confirmedAtNode is not null && !IsUtcTimestamp(confirmedAtNode))
        {
            errors[$"provenance.{path}.confirmed_at"] = "confirmed_at must be a UTC timestamp.";
            return null;
        }

        var confirmedAt = Json.AsString(confirmedAtNode);
        var confirmedByNode = raw["confirmed_by"];
        if (confirmedByNode is not null && !Json.IsString(confirmedByNode))
        {
            errors[$"provenance.{path}.confirmed_by"] = "confirmed_by must be a string.";
            return null;
        }

        // Blank is nobody: kept distinct from absent so "" cannot sneak
        // through a not-null check.
        var confirmedBy = Json.AsString(confirmedByNode);
        if (confirmedBy is not null && PyText.Strip(confirmedBy).Length == 0)
        {
            confirmedBy = null;
        }

        // INVARIANT 2. Both halves: the moment AND the person.
        if (ConfirmationRequired.Contains(source) && (confirmedAt is null || confirmedBy is null))
        {
            errors[$"provenance.{path}"] = $"A {source.ToWire()} value must be confirmed by a person before it can be stored.";
            return null;
        }

        double? confidence = null;
        var confidenceNode = raw["confidence"];
        if (confidenceNode is not null)
        {
            if (!Json.TryGetNumber(confidenceNode, out var number))
            {
                errors[$"provenance.{path}.confidence"] = "confidence must be a number.";
                return null;
            }

            if (!double.IsFinite(number) || number < 0.0 || number > 1.0)
            {
                errors[$"provenance.{path}.confidence"] = "confidence must be between 0 and 1.";
                return null;
            }

            confidence = number;
        }

        var locator = raw["locator"];
        if (locator is not null && !CheckLocator(locator, $"provenance.{path}", errors))
        {
            return null;
        }

        string? documentId = null, extractionId = null, libraryCreditorId = null, clientId = null, copiedFromCaseId = null;
        foreach (var (key, assign) in new (string, Action<string?>)[]
        {
            ("document_id", v => documentId = v),
            ("extraction_id", v => extractionId = v),
            ("library_creditor_id", v => libraryCreditorId = v),
            ("client_id", v => clientId = v),
            ("copied_from_case_id", v => copiedFromCaseId = v),
        })
        {
            var node = raw[key];
            if (node is not null && !Json.IsString(node))
            {
                errors[$"provenance.{path}.{key}"] = $"{key} must be a string.";
                return null;
            }

            assign(Json.AsString(node));
        }

        return new ProvenanceEntry(source)
        {
            ConfirmedBy = confirmedBy,
            ConfirmedAt = confirmedAt,
            DocumentId = documentId,
            Locator = (JsonObject?)locator?.DeepClone(),
            ExtractionId = extractionId,
            Confidence = confidence,
            LibraryCreditorId = libraryCreditorId,
            ClientId = clientId,
            CopiedFromCaseId = copiedFromCaseId,
        };
    }
}
