using Insolvia.Core.Cases;
using Insolvia.Core.Clients;
using Insolvia.Core.Firms;

namespace Insolvia.Core.Access;

/// <summary>
/// An authenticated caller whose firm has been resolved (from <c>access.py</c>).
/// Constructed only from a resolved firm and firm user, so holding one means:
/// authenticated, in a firm, active in it. It holds the whole
/// <see cref="FirmUser"/> so a permission check reads the same record the
/// administration screen writes.
/// </summary>
public sealed record Accessor(Firm Firm, FirmUser User)
{
    /// <summary>The caller's Cognito subject.</summary>
    public string Subject => User.Subject;

    /// <summary>The caller's firm.</summary>
    public string FirmId => Firm.Id;

    /// <summary>
    /// Whether this caller reaches their firm's cases without being linked to
    /// them one by one: <c>IsAdmin</c> or <c>AccessAllCases</c> — two
    /// independent reasons that stay independent. Also which listing a case
    /// query uses (by firm vs by assignee).
    /// </summary>
    public bool SeesEveryCase => User.IsAdmin || User.AccessAllCases;

    /// <summary>Per-feature permission, delegated so the fail-closed rules live in one place.</summary>
    public bool May(Feature feature, PermissionLevel level) => Permissions.Permits(User, feature, level);
}

/// <summary>
/// A signed-in CLIENT (ADR 0023) whose live binding has been resolved. A
/// SEPARATE TYPE, NOT an <see cref="Accessor"/> and not a subclass: every store
/// method reaching case data takes an <see cref="Accessor"/>, and a client must
/// reach none of them — the compiler refuses the call.
/// </summary>
public sealed record ClientAccessor(Firm Firm, ClientBinding Binding)
{
    /// <summary>The client's Cognito subject.</summary>
    public string Subject => Binding.Subject;

    /// <summary>The binding's firm.</summary>
    public string FirmId => Binding.FirmId;

    /// <summary>The one case the binding names.</summary>
    public string CaseId => Binding.CaseId;

    /// <summary>The filing roles the client answers for.</summary>
    public ValueCollection<ClientRole> Roles => Binding.Roles;
}

/// <summary>THE ONE PLACE THE CASE ACCESS RULE IS WRITTEN DOWN.</summary>
public static class CaseAccess
{
    /// <summary>
    /// Whether <paramref name="accessor"/> may see a case. The tenant boundary
    /// first — another firm's case is invisible to everyone, admin included —
    /// then deletion (#355: a deleted case answers as one that never existed),
    /// then the linkage rule. <paramref name="assigned"/> is a FACT THE CALLER
    /// MUST SUPPLY, read from the case's assignee record in the same round trip
    /// as the case; passing true without reading it is the way to break this.
    /// </summary>
    /// <remarks>
    /// The two facts of the case the rule reads; <see cref="MaySeeCase(Accessor, Case, bool)"/>
    /// is the overload a store holding the case calls.
    /// </remarks>
    public static bool MaySeeCase(Accessor accessor, string caseFirmId, bool caseDeleted, bool assigned)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        if (caseFirmId != accessor.FirmId)
        {
            return false;
        }

        if (caseDeleted)
        {
            return false;
        }

        return accessor.SeesEveryCase || assigned;
    }

    /// <summary>
    /// Python's <c>may_see_case(accessor, case, assigned=...)</c>: the rule
    /// above, over the case's firm and its deletion stamp.
    /// </summary>
    public static bool MaySeeCase(Accessor accessor, Case target, bool assigned)
    {
        ArgumentNullException.ThrowIfNull(target);
        return MaySeeCase(accessor, target.FirmId, target.Deleted, assigned);
    }
}
