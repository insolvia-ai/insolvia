using Insolvia.Core.Filings;

namespace Insolvia.Core.Ports;

/// <summary>
/// The filing records (ADR 0024 PR 7/8; Python's <c>FilingStore</c>). Written
/// by the filing worker and, for a hand-back's resolution, the API. Every
/// write is conditional.
/// </summary>
public interface IFilingStore
{
    /// <summary>Strongly consistent.</summary>
    Task<Filing?> GetAsync(string caseId, string filingId, CancellationToken cancellationToken = default);

    /// <summary>Create the record, conditional on there being none. True for exactly one of two racing consumers.</summary>
    Task<bool> ClaimAsync(Filing filing, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write <paramref name="filing"/>, conditional on the stored record still
    /// being in <paramref name="expectedState"/> AND carrying its attempt id.
    /// With <paramref name="filedCase"/>, the case moves to filed and its history
    /// row is written IN THE SAME TRANSACTION, conditional on the case still
    /// existing in its firm with the expected status. False when any condition
    /// failed — and then nothing was written.
    /// </summary>
    Task<bool> TransitionAsync(Filing filing, FilingState expectedState, FiledCase? filedCase = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write <paramref name="filing"/> carrying its resolution, conditional as
    /// <see cref="TransitionAsync"/> is AND on there being no resolution yet.
    /// </summary>
    Task<bool> ResolveAsync(Filing filing, FilingState expectedState, FiledCase? filedCase = null, CancellationToken cancellationToken = default);
}
