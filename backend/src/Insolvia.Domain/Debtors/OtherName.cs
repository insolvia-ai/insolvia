using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.Fields;
using Insolvia.Core.Provenance;

namespace Insolvia.Core.Debtors;

// The two pieces of debtors.py the FIRM side needs before the case half is
// ported (18.7): the 8-year alias row and its parser, which a firm client's
// `other_names_used` shares with a debtor's (ADR 0022 copies one onto the
// other, so both must accept exactly the same rows), and the filing roles a
// client merge re-points. 18.7 ports the rest of debtors.py beside these.

/// <summary>A debtor's role on the petition (Python's <c>FILING_ROLES</c>).</summary>
public enum FilingRole
{
    /// <summary>The first debtor.</summary>
    [JsonStringEnumMemberName("debtor_1")]
    Debtor1,

    /// <summary>The second (joint) debtor.</summary>
    [JsonStringEnumMemberName("debtor_2")]
    Debtor2,

    /// <summary>A spouse who is not filing (income only).</summary>
    [JsonStringEnumMemberName("non_filing_spouse")]
    NonFilingSpouse,
}

/// <summary>
/// An 8-year-lookback alias. Carries its own client-chosen <see cref="Id"/>
/// so provenance addresses it as <c>other_names_used[&lt;id&gt;].surname</c>
/// — position would reattach on reorder.
/// </summary>
public sealed record OtherName(string Id, string? Given = null, string? Middle = null, string? Surname = null, string? BusinessName = null);

/// <summary>The alias-list parser (Python's <c>debtors.parse_other_names</c>).</summary>
public static class OtherNames
{
    /// <summary>
    /// The 8-year alias list. Each row's <c>id</c> is REQUIRED and supplied by
    /// the caller — minting one here would demand provenance at a path the
    /// caller cannot have sent, and every retry would mint another.
    /// </summary>
    public static ValueCollection<OtherName> Parse(JsonNode? value, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return [];
        }

        if (value is not JsonArray list)
        {
            errors["other_names_used"] = "Must be a list.";
            return [];
        }

        var names = new List<OtherName>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < list.Count; index++)
        {
            var path = $"other_names_used[{index}]";
            var entry = FieldParsers.Mapping(list[index], path, errors);
            var givenId = Json.AsString(entry["id"]);
            if (givenId is null || !ProvenanceRules.AddressableId().IsMatch(givenId))
            {
                errors[$"{path}.id"] = "Required, and must be letters, digits, hyphen or underscore — generate one per row so provenance can name it.";
                continue;
            }

            if (!seen.Add(givenId))
            {
                errors[$"{path}.id"] = "Duplicate id.";
                continue;
            }

            names.Add(new OtherName(
                Id: givenId,
                Given: FieldParsers.Text(entry["given"], $"{path}.given", errors),
                Middle: FieldParsers.Text(entry["middle"], $"{path}.middle", errors),
                Surname: FieldParsers.Text(entry["surname"], $"{path}.surname", errors),
                BusinessName: FieldParsers.Text(entry["business_name"], $"{path}.business_name", errors)));
        }

        return names.ToValueCollection();
    }
}
