using Insolvia.Core.CaseEntities;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="ICaseEntityStore"/>. Keyed by (case, kind prefix,
/// id) — the case scope and the kind scope are properties of the map.
/// </summary>
public sealed class MemoryCaseEntityStore : ICaseEntityStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, string Prefix, string Id), ICaseEntity> _rows = [];

    /// <inheritdoc />
    public Task CreateAsync(ICaseEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        lock (_gate)
        {
            // An existing (case, kind, id) means the id minting is broken.
            if (!_rows.TryAdd(Key(entity), entity))
            {
                throw new InvalidOperationException("entity id already exists in this case");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ICaseEntity?> GetAsync(string caseId, IEntityKind kind, string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        lock (_gate)
        {
            return Task.FromResult(_rows.GetValueOrDefault((caseId, kind.SkPrefix, entityId)));
        }
    }

    /// <inheritdoc />
    public Task<bool> PutAsync(ICaseEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        lock (_gate)
        {
            var key = Key(entity);
            if (!_rows.ContainsKey(key))
            {
                return Task.FromResult(false);
            }

            _rows[key] = entity;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string caseId, IEntityKind kind, string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        lock (_gate)
        {
            return Task.FromResult(_rows.Remove((caseId, kind.SkPrefix, entityId)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ICaseEntity>> ListForCaseAsync(string caseId, IEntityKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        lock (_gate)
        {
            IReadOnlyList<ICaseEntity> listed = [.. _rows
                .Where(pair => pair.Key.CaseId == caseId && pair.Key.Prefix == kind.SkPrefix)
                .Select(pair => pair.Value)
                .Order(CaseEntityRules.ListOrderComparer)];
            return Task.FromResult(listed);
        }
    }

    private static (string, string, string) Key(ICaseEntity entity) => (entity.CaseId, entity.Kind.SkPrefix, entity.Id);
}
