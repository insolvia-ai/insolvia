using Insolvia.Core.Candidates;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists candidates — proposals awaiting review (Python's
/// <c>CandidateStore</c>). Case-scoped, not owner-scoped: the case is half the key.
/// </summary>
public interface ICandidateStore
{
    /// <summary>Store a new row; MUST throw rather than replace an existing (case, id).</summary>
    Task CreateAsync(Candidate candidate, CancellationToken cancellationToken = default);

    /// <summary>One candidate, or null.</summary>
    Task<Candidate?> GetAsync(string caseId, string candidateId, CancellationToken cancellationToken = default);

    /// <summary>Every candidate of one case, in creation order. Status filtering happens above this port.</summary>
    Task<IReadOnlyList<Candidate>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write <paramref name="candidate"/> back only if the stored status is
    /// still <paramref name="expectedStatus"/> — the compare-and-swap review and
    /// withdrawal ride on. Null means the race was lost.
    /// </summary>
    Task<Candidate?> UpdateAsync(Candidate candidate, CandidateStatus expectedStatus, CancellationToken cancellationToken = default);
}
