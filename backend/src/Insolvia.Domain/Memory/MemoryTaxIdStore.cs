using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="ITaxIdStore"/>. Keyed by (case, ref) — location plus
/// identity — so a read under the wrong case answers null, as the real store does.
/// </summary>
public sealed class MemoryTaxIdStore : ITaxIdStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, string Ref), SealedTaxId> _items = [];

    /// <summary>A snapshot of every sealed item — a test affordance.</summary>
    public IReadOnlyDictionary<(string CaseId, string Ref), SealedTaxId> Items
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<(string, string), SealedTaxId>(_items);
            }
        }
    }

    /// <inheritdoc />
    public Task PutAsync(string caseId, SealedTaxId sealedId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sealedId);
        lock (_gate)
        {
            _items[(caseId, sealedId.Ref)] = sealedId;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SealedTaxId?> GetAsync(string caseId, string reference, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.GetValueOrDefault((caseId, reference)));
        }
    }
}
