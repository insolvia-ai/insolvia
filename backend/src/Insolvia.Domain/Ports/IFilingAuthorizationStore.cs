using Insolvia.Core.FilingAuthorizations;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists attorneys' signed authorizations (Python's
/// <c>FilingAuthorizationStore</c>): THE CURRENT ONE, and one history record
/// per signature.
/// </summary>
public interface IFilingAuthorizationStore
{
    /// <summary>The current authorization, strongly consistent, or null.</summary>
    Task<FilingAuthorization?> GetCurrentAsync(string firmId, string attorneyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Make <paramref name="authorization"/> current and write its history
    /// record, conditional on <paramref name="replacing"/> (what the caller read;
    /// null for none) still being current — a lost race throws
    /// <see cref="ConflictException"/>. A replaced record's history becomes superseded.
    /// </summary>
    Task PutCurrentAsync(FilingAuthorization authorization, FilingAuthorization? replacing, CancellationToken cancellationToken = default);

    /// <summary>Remove the current item if it is still <paramref name="authorization"/>, and mark its history withdrawn. True when it was.</summary>
    Task<bool> WithdrawAsync(FilingAuthorization authorization, DateTimeOffset withdrawnAt, CancellationToken cancellationToken = default);
}
