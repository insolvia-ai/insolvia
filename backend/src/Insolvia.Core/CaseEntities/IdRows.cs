using System.Text.Json.Nodes;
using Insolvia.Core.Fields;
using Insolvia.Core.Provenance;

namespace Insolvia.Core.CaseEntities;

/// <summary>
/// A list of rows with client-chosen addressable ids, unique per list — the
/// shape every embedded list on a case record shares (Python's per-module
/// <c>_rows</c> / <c>_parse_deductions</c> / <c>_parse_described_amounts</c>).
/// The id is REQUIRED and supplied by the caller: minting one would demand
/// provenance at a path the caller cannot have sent.
/// </summary>
public static class IdRows
{
    /// <summary>The message for a missing or malformed row id.</summary>
    public const string IdRequired =
        "Required, and must be letters, digits, hyphen or underscore — generate one per row so provenance can name it.";

    /// <summary>
    /// Parses <paramref name="value"/> as rows of <paramref name="fieldName"/>.
    /// With <paramref name="skipNonObjects"/> a non-object row is refused under
    /// its own path and skipped (plans, means test); without it the row is read
    /// as an empty object (<c>fields.mapping</c>'s rule — income, claims), so
    /// its missing id is reported too.
    /// </summary>
    public static ValueCollection<TRow> Parse<TRow>(
        JsonNode? value,
        string fieldName,
        IDictionary<string, string> errors,
        Func<string, JsonObject, string, TRow> build,
        bool skipNonObjects = true)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(build);
        if (value is null)
        {
            return [];
        }

        if (value is not JsonArray list)
        {
            errors[fieldName] = "Must be a list.";
            return [];
        }

        var rows = new List<TRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < list.Count; index++)
        {
            var path = $"{fieldName}[{index}]";
            JsonObject raw;
            if (skipNonObjects)
            {
                if (list[index] is not JsonObject obj)
                {
                    errors[path] = "Must be an object.";
                    continue;
                }

                raw = obj;
            }
            else
            {
                raw = FieldParsers.Mapping(list[index], path, errors);
            }

            var givenId = Json.AsString(raw["id"]);
            if (givenId is null || !ProvenanceRules.AddressableId().IsMatch(givenId))
            {
                errors[$"{path}.id"] = IdRequired;
                continue;
            }

            if (!seen.Add(givenId))
            {
                errors[$"{path}.id"] = "Duplicate id.";
                continue;
            }

            rows.Add(build(givenId, raw, path));
        }

        return rows.ToValueCollection();
    }

    /// <summary>Throws the accumulated errors, if any.</summary>
    public static void ThrowIfAny(IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count > 0)
        {
            throw new FieldValidationException(new Dictionary<string, string>(errors, StringComparer.Ordinal));
        }
    }
}
