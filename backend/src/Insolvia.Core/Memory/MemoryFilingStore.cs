using Insolvia.Core.Cases;
using Insolvia.Core.Filings;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IFilingStore"/>. A write that files the case does
/// its case half through <paramref name="cases"/> under this store's lock, so
/// the filing and the case move together or not at all.
/// </summary>
/// <param name="cases">The case store the filed case lives in; a private one when none is given.</param>
public sealed class MemoryFilingStore(MemoryCaseStore? cases = null) : IFilingStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, string FilingId), Filing> _items = [];

    /// <summary>The case store this filing store files into.</summary>
    public MemoryCaseStore Cases { get; } = cases ?? new MemoryCaseStore();

    /// <summary>A snapshot of every filing — a test affordance.</summary>
    public IReadOnlyList<Filing> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items.Values];
            }
        }
    }

    /// <inheritdoc />
    public Task<Filing?> GetAsync(string caseId, string filingId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.GetValueOrDefault((caseId, filingId)));
        }
    }

    /// <inheritdoc />
    public Task<bool> ClaimAsync(Filing filing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filing);
        lock (_gate)
        {
            return Task.FromResult(_items.TryAdd((filing.CaseId, filing.FilingId), filing));
        }
    }

    /// <inheritdoc />
    public Task<bool> TransitionAsync(Filing filing, FilingState expectedState, LifecycleWrite? filedCase = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Write(filing, expectedState, filedCase, unresolved: false));

    /// <inheritdoc />
    public Task<bool> ResolveAsync(Filing filing, FilingState expectedState, LifecycleWrite? filedCase = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Write(filing, expectedState, filedCase, unresolved: true));

    private bool Write(Filing filing, FilingState expectedState, LifecycleWrite? filedCase, bool unresolved)
    {
        ArgumentNullException.ThrowIfNull(filing);
        if (filedCase is not null && (!filedCase.FilesTheCase || filedCase.Case.Id != filing.CaseId))
        {
            throw new ArgumentException("a filing's case half moves its own case into filed", nameof(filedCase));
        }

        lock (_gate)
        {
            var key = (filing.CaseId, filing.FilingId);
            if (!_items.TryGetValue(key, out var stored)
                || stored.State != expectedState
                || stored.AttemptId != filing.AttemptId
                || (unresolved && stored.Resolution is not null))
            {
                return false;
            }

            if (filedCase is not null && !Cases.TryFile(filedCase))
            {
                return false;
            }

            _items[key] = filing;
            return true;
        }
    }
}
