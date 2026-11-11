using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Exemptions;

/// <summary>
/// The claimed exemption — 106C, one row per exemption asserted on an asset
/// (from <c>exemption_claims.py</c>). <see cref="Amount"/> and
/// <see cref="ClaimsFullFmv"/> are mutually exclusive, but that is a
/// completeness fact, not a shape rule. <see cref="AssetId"/> is unchecked
/// here (progressive intake). The property description and value 106C prints
/// are the asset's, copied at projection time, never stored here.
/// </summary>
public sealed record ExemptionBody(
    [property: Text(64)] string? AssetId = null,
    [property: Text] string? StatuteCitation = null,
    [property: Money] decimal? Amount = null,
    bool? ClaimsFullFmv = null,
    bool? AcquiredWithin1215Days = null);

/// <summary>The exemption collection.</summary>
public static class ExemptionRules
{
    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<ExemptionBody> Kind = new("exemption", "exemptions", "EXEMPTION");
}
