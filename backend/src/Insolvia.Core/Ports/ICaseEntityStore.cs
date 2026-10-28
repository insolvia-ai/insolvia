using Insolvia.Core.CaseEntities;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists the generic case collections (issue #249; Python's
/// <c>CaseEntityStore</c>) — one port for every kind, because they are one
/// shape. Ownership is NOT a parameter; the case scope IS enforced: the case
/// is half the key, so an id from another case does not resolve.
/// </summary>
public interface ICaseEntityStore
{
    /// <summary>Store a new record; MUST throw rather than overwrite an existing (case, kind, id).</summary>
    Task CreateAsync(ICaseEntity entity, CancellationToken cancellationToken = default);

    /// <summary>One record of <paramref name="kind"/>, or null.</summary>
    Task<ICaseEntity?> GetAsync(string caseId, IEntityKind kind, string entityId, CancellationToken cancellationToken = default);

    /// <summary>Replace the record only over one that still exists — false when it was gone (an edit racing a delete).</summary>
    Task<bool> PutAsync(ICaseEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Remove the record. True if this call removed it.</summary>
    Task<bool> DeleteAsync(string caseId, IEntityKind kind, string entityId, CancellationToken cancellationToken = default);

    /// <summary>Every record of one collection in one case, in <see cref="CaseEntityRules.ListOrder"/>. All of them.</summary>
    Task<IReadOnlyList<ICaseEntity>> ListForCaseAsync(string caseId, IEntityKind kind, CancellationToken cancellationToken = default);
}

/// <summary>Typed reads over <see cref="ICaseEntityStore"/>.</summary>
public static class CaseEntityStoreExtensions
{
    /// <summary>One record of <paramref name="kind"/>, typed.</summary>
    public static async Task<CaseEntity<TBody>?> GetTypedAsync<TBody>(this ICaseEntityStore store, string caseId, EntityKind<TBody> kind, string entityId, CancellationToken cancellationToken = default)
        where TBody : class
    {
        ArgumentNullException.ThrowIfNull(store);
        return (CaseEntity<TBody>?)await store.GetAsync(caseId, (IEntityKind)kind, entityId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every record of <paramref name="kind"/> in one case, typed.</summary>
    public static async Task<IReadOnlyList<CaseEntity<TBody>>> ListTypedAsync<TBody>(this ICaseEntityStore store, string caseId, EntityKind<TBody> kind, CancellationToken cancellationToken = default)
        where TBody : class
    {
        ArgumentNullException.ThrowIfNull(store);
        var entities = await store.ListForCaseAsync(caseId, (IEntityKind)kind, cancellationToken).ConfigureAwait(false);
        return [.. entities.Cast<CaseEntity<TBody>>()];
    }
}
