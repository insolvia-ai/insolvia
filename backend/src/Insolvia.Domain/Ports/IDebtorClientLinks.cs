using Insolvia.Core.Debtors;

namespace Insolvia.Core.Ports;

/// <summary>
/// The three things a client merge asks of the debtor store (Python's
/// <c>DebtorStore.roles_for_client</c>, <c>repoint_client</c> and — for the
/// joint-case pre-check — <c>list_for_case</c>'s link facts). A seam narrower
/// than the debtor store so the firm half can be ported before the case half:
/// 18.7's <c>IDebtorStore</c> should EXTEND this interface, and its memory and
/// Postgres stores then serve <see cref="FirmClients.ClientMerges"/> unchanged.
/// </summary>
public interface IDebtorClientLinks
{
    /// <summary>
    /// Every (case, role) whose debtor names <paramref name="clientId"/>, sorted
    /// by case id then role — the <c>by-client</c> index, followed to its last page.
    /// </summary>
    Task<IReadOnlyList<(string CaseId, FilingRole FilingRole)>> RolesForClientAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Move one debtor's link from <paramref name="fromClientId"/> to
    /// <paramref name="toClientId"/> — only the link; copied fields and their
    /// provenance stay. One conditional write: <see cref="RepointOutcome.Absent"/>
    /// when the role no longer names the merged client,
    /// <see cref="RepointOutcome.ClientTaken"/> when the survivor holds another
    /// role on the case. A concurrent write that cancels it is a
    /// <see cref="ConflictException"/>.
    /// </summary>
    Task<RepointOutcome> RepointClientAsync(string caseId, FilingRole filingRole, string fromClientId, string toClientId, CancellationToken cancellationToken = default);

    /// <summary>Every debtor on <paramref name="caseId"/>, as the client each is linked to.</summary>
    Task<IReadOnlyList<DebtorClientLink>> ClientLinksForCaseAsync(string caseId, CancellationToken cancellationToken = default);
}
