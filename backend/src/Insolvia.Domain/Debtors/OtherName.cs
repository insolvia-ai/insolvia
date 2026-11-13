using System.Text.Json.Serialization;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Debtors;

// The two pieces of debtors.py the FIRM side shares: the 8-year alias row,
// which a firm client's `otherNamesUsed` shares with a debtor's (ADR 0022
// copies one onto the other, so both must accept exactly the same rows), and
// the filing roles a client merge re-points.

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
/// so provenance addresses it as <c>otherNamesUsed[&lt;id&gt;].surname</c>
/// — position would reattach on reorder. <see cref="BusinessName"/> is
/// <c>businessName</c> under every serializer, the stored client body's
/// snake_case one included.
/// </summary>
public sealed record OtherName(
    string Id,
    [property: Text] string? Given = null,
    [property: Text] string? Middle = null,
    [property: Text] string? Surname = null,
    [property: Text, JsonPropertyName("businessName")] string? BusinessName = null) : IRow;
