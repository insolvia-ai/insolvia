using System.Text.Json.Nodes;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Creditors;

/// <summary>
/// One deduplicated name-and-address for the creditor matrix (from
/// <c>creditors.py</c>). Separate from a claim on purpose: a debtor may owe
/// one creditor twice. The name is ONE line, not a <see cref="PersonName"/>
/// — creditors are predominantly entities. Nothing here dedupes; matching is
/// a suggestion to a human.
/// </summary>
public sealed record CreditorBody(string? Name, Address Address);

/// <summary>The creditor collection.</summary>
public static class CreditorRules
{
    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<CreditorBody> Kind = new("creditor", "creditors", "CREDITOR", Parse);

    /// <summary>Validates a creditor body (Python's <c>parse_creditor</c>).</summary>
    public static CreditorBody Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new CreditorBody(
            Name: FieldParsers.Text(payload["name"], "name", errors),
            Address: FieldParsers.ParseAddress(payload["address"], "address", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }
}
