using Insolvia.Core.Debtors;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists the debtor records of a case (issue 8.5; Python's
/// <c>DebtorStore</c>). Ownership is NOT a parameter: a debtor is reached
/// only through its case, resolved through <see cref="ICaseStore"/> first.
/// Extends <see cref="IDebtorClientLinks"/>, the three questions a client
/// merge asks.
/// </summary>
public interface IDebtorStore : IDebtorClientLinks
{
    /// <summary>
    /// Write <paramref name="debtor"/> ONLY if its case has no record for its
    /// role yet; false when one exists, having written nothing — the id the
    /// first save handed back must stay stable.
    /// </summary>
    Task<bool> CreateAsync(Debtor debtor, CancellationToken cancellationToken = default);

    /// <summary>Replace the record for the debtor's role outright.</summary>
    Task PutAsync(Debtor debtor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write <paramref name="debtor"/> — which names a client — in ONE
    /// conditional write: only if no other role of its case names the same
    /// client, with <paramref name="create"/> only if its own role is empty, and
    /// only if the client's row in <paramref name="firmId"/> is still linkable.
    /// Nothing written for any answer but <see cref="LinkOutcome.Written"/>.
    /// </summary>
    Task<LinkOutcome> LinkAsync(Debtor debtor, bool create, string firmId, CancellationToken cancellationToken = default);

    /// <summary>The debtor in <paramref name="filingRole"/>, or null.</summary>
    Task<Debtor?> GetAsync(string caseId, FilingRole filingRole, CancellationToken cancellationToken = default);

    /// <summary>Every debtor of one case, in filing-role order — the order the forms print them.</summary>
    Task<IReadOnlyList<Debtor>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default);
}
