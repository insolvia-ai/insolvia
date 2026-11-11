using System.Text.Json.Nodes;
using Insolvia.Domain.Provenance;

namespace Insolvia.Domain.CaseEntities;

// The machinery every case-scoped collection shares (issue #249; from
// case_entities.py). Every collection is the same KIND of thing — a
// many-cardinality record in its case, with server-stamped identity, a typed
// body and a per-field provenance map — so the shape is written once here and
// each entity module contributes only its body record (ADR 0032).

/// <summary>One collection's identity, without its body type — what a generic route or store holds.</summary>
public interface IEntityKind
{
    /// <summary>The entity's name, e.g. <c>creditor</c>.</summary>
    string Name { get; }

    /// <summary>The URL segment and the JSON listing key, e.g. <c>creditors</c>.</summary>
    string Collection { get; }

    /// <summary>The sort-key namespace in the case, e.g. <c>CREDITOR</c> — unique across the case's records.</summary>
    string SkPrefix { get; }

    /// <summary>The body record's type, e.g. <c>CreditorBody</c>.</summary>
    Type BodyType { get; }

    /// <summary>A record of this kind from its parts — a store's read, untyped (a store holds the kind, not the body type).</summary>
    ICaseEntity Restore(string id, string caseId, DateTimeOffset createdAt, DateTimeOffset updatedAt, object body, ValueDictionary<string, ProvenanceEntry> provenance, bool amended);
}

/// <summary>One collection's identity: what it is called, where its items sort, and its body record.</summary>
/// <typeparam name="TBody">The body record.</typeparam>
/// <param name="name">The entity's name.</param>
/// <param name="collection">The URL segment.</param>
/// <param name="skPrefix">The sort-key namespace.</param>
public sealed class EntityKind<TBody>(string name, string collection, string skPrefix) : IEntityKind
    where TBody : class
{
    /// <inheritdoc />
    public string Name { get; } = name;

    /// <inheritdoc />
    public Type BodyType => typeof(TBody);

    /// <inheritdoc />
    public string Collection { get; } = collection;

    /// <inheritdoc />
    public string SkPrefix { get; } = skPrefix;

    /// <inheritdoc />
    public ICaseEntity Restore(string id, string caseId, DateTimeOffset createdAt, DateTimeOffset updatedAt, object body, ValueDictionary<string, ProvenanceEntry> provenance, bool amended) =>
        new CaseEntity<TBody>(this, id, caseId, createdAt, updatedAt, (TBody)body, provenance, amended);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A stored collection record, untyped — what a generic route or store holds.</summary>
public interface ICaseEntity
{
    /// <summary>Which collection.</summary>
    IEntityKind Kind { get; }

    /// <summary>The server-minted id.</summary>
    string Id { get; }

    /// <summary>The case it lives in.</summary>
    string CaseId { get; }

    /// <summary>When it was created — the list order.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>When it was last written.</summary>
    DateTimeOffset UpdatedAt { get; }

    /// <summary>The body.</summary>
    object Body { get; }

    /// <summary>The per-field provenance.</summary>
    ValueDictionary<string, ProvenanceEntry> Provenance { get; }

    /// <summary>Whether the record amends a filed one (issue #370).</summary>
    bool Amended { get; }

    /// <summary>The body as JSON — what provenance paths address.</summary>
    JsonObject BodyNode();

    /// <summary>
    /// This record moved into another case (Copy case): same id, same
    /// <c>createdAt</c>, the given <paramref name="updatedAt"/> and
    /// <paramref name="provenance"/>, and never amended.
    /// </summary>
    ICaseEntity CopiedTo(string caseId, DateTimeOffset updatedAt, ValueDictionary<string, ProvenanceEntry> provenance);
}

/// <summary>A stored collection record.</summary>
/// <typeparam name="TBody">The body record.</typeparam>
public sealed record CaseEntity<TBody>(
    EntityKind<TBody> Kind,
    string Id,
    string CaseId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    TBody Body,
    ValueDictionary<string, ProvenanceEntry> Provenance,
    bool Amended = false) : ICaseEntity
    where TBody : class
{
    IEntityKind ICaseEntity.Kind => Kind;

    object ICaseEntity.Body => Body;

    /// <inheritdoc />
    public JsonObject BodyNode() => BodyJson.ToNode(Body);

    /// <inheritdoc />
    public ICaseEntity CopiedTo(string caseId, DateTimeOffset updatedAt, ValueDictionary<string, ProvenanceEntry> provenance) =>
        this with { CaseId = caseId, UpdatedAt = updatedAt, Amended = false, Provenance = provenance };
}

/// <summary>The functions every collection shares (Python's module-level functions in <c>case_entities.py</c>).</summary>
public static class CaseEntityRules
{
    /// <summary>
    /// The provenance a body must carry to be written: every populated field
    /// has an entry (<see cref="ProvenanceRules.RequireProvenance"/>), and a
    /// save may keep a server-minted entry but never mint one
    /// (<see cref="ProvenanceRules.RequireServerSourcesKept"/>). A null
    /// <paramref name="stored"/> is a create, which can keep nothing.
    /// </summary>
    public static void RequireProvenance(object body, IReadOnlyDictionary<string, ProvenanceEntry> provenance, ICaseEntity? stored)
    {
        var node = BodyJson.ToNode(body);
        ProvenanceRules.RequireProvenance(node, provenance);
        ProvenanceRules.RequireServerSourcesKept(node, provenance, stored?.BodyNode(), stored?.Provenance);
    }

    /// <summary>A new record: a fresh uuid, created and updated now.</summary>
    public static CaseEntity<TBody> Create<TBody>(EntityKind<TBody> kind, string caseId, TBody body, ValueDictionary<string, ProvenanceEntry> provenance, TimeProvider? clock = null)
        where TBody : class
    {
        var now = Clock.Now(clock);
        return new CaseEntity<TBody>(kind, Guid.NewGuid().ToString(), caseId, now, now, body, provenance);
    }

    /// <summary>
    /// Creation order, id as the tiebreak — the ONE definition, because both
    /// stores must agree and the sort key is a random uuid.
    /// </summary>
    public static (DateTimeOffset CreatedAt, string Id) ListOrder(ICaseEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return (entity.CreatedAt, entity.Id);
    }

    /// <summary>The comparer for <see cref="ListOrder"/>.</summary>
    public static IComparer<ICaseEntity> ListOrderComparer { get; } = Comparer<ICaseEntity>.Create((left, right) =>
    {
        var byTime = left.CreatedAt.CompareTo(right.CreatedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(left.Id, right.Id);
    });
}
