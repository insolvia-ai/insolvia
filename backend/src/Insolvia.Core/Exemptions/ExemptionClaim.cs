using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Exemptions;

/// <summary>
/// The claimed exemption — 106C, one row per exemption asserted on an asset
/// (from <c>exemption_claims.py</c>). <see cref="Amount"/> and
/// <see cref="ClaimsFullFmv"/> are mutually exclusive, but that is a
/// completeness fact, not a shape rule. <see cref="AssetId"/> is unchecked
/// here (progressive intake). The property description and value 106C prints
/// are the asset's, copied at projection time, never stored here.
/// </summary>
public sealed record ExemptionBody(
    string? AssetId,
    string? StatuteCitation,
    decimal? Amount,
    bool? ClaimsFullFmv,
    [property: JsonPropertyName("acquired_within_1215_days")] bool? AcquiredWithin1215Days);

/// <summary>The exemption collection.</summary>
public static class ExemptionRules
{
    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<ExemptionBody> Kind = new("exemption", "exemptions", "EXEMPTION", Parse);

    /// <summary>Validates an exemption body (Python's <c>parse_exemption</c>).</summary>
    public static ExemptionBody Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new ExemptionBody(
            AssetId: FieldParsers.Text(payload["asset_id"], "asset_id", errors, limit: 64),
            StatuteCitation: FieldParsers.Text(payload["statute_citation"], "statute_citation", errors),
            Amount: FieldParsers.Money(payload["amount"], "amount", errors),
            ClaimsFullFmv: FieldParsers.Boolean(payload["claims_full_fmv"], "claims_full_fmv", errors),
            AcquiredWithin1215Days: FieldParsers.Boolean(payload["acquired_within_1215_days"], "acquired_within_1215_days", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }
}
