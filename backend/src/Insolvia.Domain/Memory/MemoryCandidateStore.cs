using Insolvia.Core.Candidates;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="ICandidateStore"/>. Keyed by (case, id); the update
/// is the compare-and-swap on the stored status that review and withdrawal ride on.
/// </summary>
public sealed class MemoryCandidateStore : ICandidateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, string Id), Candidate> _rows = [];

    /// <inheritdoc />
    public Task CreateAsync(Candidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            if (!_rows.TryAdd((candidate.CaseId, candidate.Id), candidate))
            {
                throw new InvalidOperationException("candidate id already exists in this case");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Candidate?> GetAsync(string caseId, string candidateId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.GetValueOrDefault((caseId, candidateId)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Candidate>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Candidate> listed = [.. _rows.Where(pair => pair.Key.CaseId == caseId).Select(pair => pair.Value).Order(CandidateRules.ListOrder)];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<Candidate?> UpdateAsync(Candidate candidate, CandidateStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            var key = (candidate.CaseId, candidate.Id);
            if (!_rows.TryGetValue(key, out var stored) || stored.Status != expectedStatus)
            {
                return Task.FromResult<Candidate?>(null);
            }

            _rows[key] = candidate;
            return Task.FromResult<Candidate?>(candidate);
        }
    }
}
