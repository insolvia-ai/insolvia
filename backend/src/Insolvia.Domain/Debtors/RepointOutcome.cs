using System.Text.Json.Serialization;

namespace Insolvia.Core.Debtors;

/// <summary>
/// What re-pointing one case's debtor from a merged client to its survivor
/// answered (Python's <c>debtors.RepointOutcome</c>) — one step of a client merge.
/// </summary>
public enum RepointOutcome
{
    /// <summary>The link moved.</summary>
    [JsonStringEnumMemberName("written")]
    Written,

    /// <summary>That role no longer names the merged client (an earlier pass, or a re-link, moved it).</summary>
    [JsonStringEnumMemberName("absent")]
    Absent,

    /// <summary>The survivor already holds another role on the case — a joint case; refused.</summary>
    [JsonStringEnumMemberName("client_taken")]
    ClientTaken,
}

/// <summary>Which client, if any, a case's debtor in <paramref name="FilingRole"/> is linked to.</summary>
public sealed record DebtorClientLink(string CaseId, FilingRole FilingRole, string? ClientId);
