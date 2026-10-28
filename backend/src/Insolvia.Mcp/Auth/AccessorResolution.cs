using Insolvia.Core.Access;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using Insolvia.Mcp.Logging;

namespace Insolvia.Mcp.Auth;

/// <summary>The caller's accessor, resolved per tool call (services/mcp <c>api/auth.py</c>'s <c>resolve_accessor</c>).</summary>
public static class AccessorResolution
{
    /// <summary>
    /// The accessor for <paramref name="subject"/>, or null when they are not
    /// active in an active firm. THE TWO READS ADR 0009 COSTS, once per tool
    /// call — never cached, not even per session, because the transport is
    /// stateless and staleness here is a security property: an admin who cuts
    /// an agent's access expects it cut now. One read resolves the firm user
    /// by subject; one fetches the firm, so a SUSPENDED firm is suspended.
    /// </summary>
    /// <param name="firms">The firm store.</param>
    /// <param name="subject">The verified token's subject.</param>
    /// <param name="log">Where the unresolved reason goes (a category, never the subject).</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The accessor, or null.</returns>
    public static async Task<Accessor?> ResolveAsync(IFirmStore firms, string subject, ILogger log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firms);
        var user = await firms.FindUserAsync(subject, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Status != FirmUserStatus.Active)
        {
            log.Info("accessor unresolved", ("reason", "no_active_firm_user"));
            return null;
        }

        var firm = await firms.GetFirmAsync(user.FirmId, cancellationToken).ConfigureAwait(false);
        if (firm is null || firm.Status != FirmStatus.Active)
        {
            log.Info("accessor unresolved", ("reason", "firm_not_active"));
            return null;
        }

        return new Accessor(firm, user);
    }
}
