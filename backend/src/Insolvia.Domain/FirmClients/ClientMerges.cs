using Insolvia.Core.Ports;

namespace Insolvia.Core.FirmClients;

/// <summary>
/// What a finished merge leaves: the survivor as it now stands and the merged
/// client, archived with <c>MergedInto</c>. Deliberately NO count of the cases
/// moved — it would include cases the person merging cannot see.
/// </summary>
public sealed record MergeResult(FirmClient Survivor, FirmClient Merged);

/// <summary>
/// Merging two firm clients (ADR 0022's PR 7 / #354; from <c>client_merge.py</c>).
/// The merged client B is folded into the survivor A: every case whose debtor
/// names B is re-pointed to A, and B is archived with <c>MergedInto = A</c>,
/// never deleted. A debtor's copied fields and their provenance are NOT
/// touched — "copied from B, which was merged into A" is the truthful history.
/// </summary>
/// <remarks>
/// A merge first CLAIMS both rows in one conditional write, so two merges into
/// and out of the same client cannot strand a case; the per-case writes run
/// under the claim, and the last write archives B and releases both halves.
/// A claim for the same pair passes again, so a merge that stopped half-way is
/// finished by running it again. Two clients who are both debtors on one case
/// are REFUSED before anything is written: which role should win is the firm's
/// call, not the merge's.
/// </remarks>
public static class ClientMerges
{
    /// <summary>The joint-case refusal.</summary>
    public const string JointCase =
        "These two clients are both debtors on the same case. Link that case's debtor to the right client first, then merge.";

    /// <summary>The refusal while another merge involving either client is in flight.</summary>
    public const string InProgress =
        "Another merge involving one of these clients is in progress. Try again when it has finished.";

    /// <summary>
    /// Why <paramref name="merged"/> cannot be folded into
    /// <paramref name="survivor"/> as the two rows stand, or null. A pre-check
    /// for a clear message; the claim's own condition is what decides.
    /// </summary>
    public static string? Refusal(FirmClient survivor, FirmClient merged)
    {
        ArgumentNullException.ThrowIfNull(survivor);
        ArgumentNullException.ThrowIfNull(merged);
        if (merged.MergedInto is not null)
        {
            return "That client was already merged into another client.";
        }

        if (survivor.MergedInto is not null)
        {
            return "This client was merged into another client and cannot receive a merge.";
        }

        if (merged.Archived || survivor.Archived)
        {
            return "An archived client cannot be merged — restore them first.";
        }

        var inFlight = merged.MergingFrom is not null
            || (merged.MergingInto is not null && merged.MergingInto != survivor.Id)
            || survivor.MergingInto is not null
            || (survivor.MergingFrom is not null && survivor.MergingFrom != merged.Id);
        return inFlight ? InProgress : null;
    }

    /// <summary>
    /// Fold <paramref name="merged"/> into <paramref name="survivor"/>, both
    /// already resolved in one firm. Throws <see cref="FieldValidationException"/>
    /// for a client merged into itself and <see cref="ConflictException"/> for
    /// every refusal about the clients' state.
    /// </summary>
    public static async Task<MergeResult> MergeAsync(
        IFirmStore firmStore,
        IDebtorClientLinks debtors,
        FirmClient survivor,
        FirmClient merged,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firmStore);
        ArgumentNullException.ThrowIfNull(debtors);
        ArgumentNullException.ThrowIfNull(survivor);
        ArgumentNullException.ThrowIfNull(merged);
        if (survivor.FirmId != merged.FirmId)
        {
            throw new InvalidOperationException("a merge's clients must be resolved in one firm");
        }

        if (survivor.Id == merged.Id)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["merged_client_id"] = "A client cannot be merged into itself." });
        }

        if (Refusal(survivor, merged) is { } reason)
        {
            throw new ConflictException(reason);
        }

        var firmId = survivor.FirmId;

        // Before the claim, so a refusal leaves nothing to release.
        if (await SharesACaseAsync(debtors, survivor.Id, merged.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException(JointCase);
        }

        if (!await firmStore.ClaimClientMergeAsync(firmId, merged.Id, survivor.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException(InProgress);
        }

        // Twice over the index: the second pass catches an entry the first
        // missed because the index lagged a write that landed just before the
        // claim. Entries already moved answer Absent.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var (caseId, role) in await debtors.RolesForClientAsync(merged.Id, cancellationToken).ConfigureAwait(false))
            {
                var outcome = await debtors.RepointClientAsync(caseId, role, merged.Id, survivor.Id, cancellationToken).ConfigureAwait(false);
                if (outcome == Debtors.RepointOutcome.ClientTaken)
                {
                    await firmStore.ReleaseClientMergeAsync(firmId, merged.Id, survivor.Id, cancellationToken).ConfigureAwait(false);
                    throw new ConflictException(JointCase);
                }
            }
        }

        var archived = await firmStore.FinishClientMergeAsync(firmId, merged.Id, survivor.Id, Clock.Now(clock), cancellationToken).ConfigureAwait(false);
        if (archived is null)
        {
            // A concurrent run of this same merge finished first; answer what it left.
            archived = await firmStore.GetClientAsync(firmId, merged.Id, cancellationToken).ConfigureAwait(false);
            if (archived is null || archived.MergedInto != survivor.Id)
            {
                throw new ConflictException(InProgress);
            }
        }

        var current = await firmStore.GetClientAsync(firmId, survivor.Id, cancellationToken).ConfigureAwait(false);
        return new MergeResult(current ?? survivor, archived);
    }

    private static async Task<bool> SharesACaseAsync(IDebtorClientLinks debtors, string survivorId, string mergedId, CancellationToken cancellationToken)
    {
        foreach (var (caseId, role) in await debtors.RolesForClientAsync(mergedId, cancellationToken).ConfigureAwait(false))
        {
            foreach (var link in await debtors.ClientLinksForCaseAsync(caseId, cancellationToken).ConfigureAwait(false))
            {
                if (link.FilingRole != role && link.ClientId == survivorId)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
