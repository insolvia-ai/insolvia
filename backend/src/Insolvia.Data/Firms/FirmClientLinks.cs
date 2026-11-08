using Insolvia.Core.FirmClients;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Firms;

/// <summary>
/// The case stores' half of a client link (Python's <c>client_link_condition</c>,
/// a <c>ConditionCheck</c> item the case and debtor stores put in their
/// transactions). Owned here, beside the merge claim's condition on the same
/// row, so the two cannot drift. For 18.11: call it INSIDE the transaction
/// that writes the link, and refuse the write when it answers false.
/// </summary>
internal static class FirmClientLinks
{
    /// <summary>
    /// Whether the client may be linked to a case right now — it exists in
    /// this firm, is active (every merged client is archived), was never
    /// merged and is not being merged away; a client RECEIVING a merge passes.
    /// The row is locked <c>FOR SHARE</c> until the caller's transaction ends,
    /// so a merge claim cannot land between this check and the link.
    /// </summary>
    public static async Task<bool> LockLinkableAsync(InsolviaDbContext db, string firmId, string clientId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var active = FirmClientStatus.Active;
        var rows = await db.Set<FirmClientRow>()
            .FromSqlInterpolated($"SELECT * FROM insolvia.firm_clients WHERE firm_id = {firmId} AND id = {clientId} FOR SHARE")
            .AsNoTracking()
            .Where(client => client.Status == active && client.MergedInto == null && client.MergingInto == null)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows > 0;
    }
}
