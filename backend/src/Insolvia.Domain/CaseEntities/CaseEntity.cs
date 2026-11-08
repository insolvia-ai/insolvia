using System.Text.Json.Nodes;
using Insolvia.Core.Provenance;

namespace Insolvia.Core.CaseEntities;

// The machinery every case-scoped collection shares (issue #249; from
// case_entities.py). Every collection is the same KIND of thing — a
// many-cardinality record in its case, with server-stamped identity, a
// validated body and a per-field provenance map — so the shape is written
// once here and each entity module contributes only its body and parser.
// The provenance invariants are INHERITED: ParseEntity runs
// RequireProvenance over the parsed body, so no collection can forget it.

/// <summary>One collection's identity, without its body type — what a generic route or store holds.</summary>
public interface IEntityKind
{
    /// <summary>The entity's name, e.g. <c>creditor</c>.</summary>
    string Name { get; }

    /// <summary>The URL segment and the JSON listing key, e.g. <c>creditors</c>.</summary>
    string Collection { get; }

    /// <summary>The sort-key namespace in the case, e.g. <c>CREDITOR</c> — unique across the case's records.</summary>
    string SkPrefix { get; }

    /// <summary><see cref="CaseEntityRules.ParseEntity{TBody}"/> for this kind, untyped.</summary>
    IEntityDraft ParseDraft(JsonObject payload, bool enforceProvenance = true);

    /// <summary>
    /// A stored record rebuilt from its identity and a draft this kind parsed
    /// — a store's read, untyped (a store holds the kind, not the body type).
    /// </summary>
    ICaseEntity Restore(string id, string caseId, DateTimeOffset createdAt, DateTimeOffset updatedAt, IEntityDraft draft);
}

/// <summary>
/// One collection's identity: what it is called, where its items sort, and how
/// a body is validated. <see cref="ParseBody"/> validates SHAPE AND TYPE ONLY,
/// accepts absent values everywhere (progressive intake), and raises
/// <see cref="FieldValidationException"/> with every malformed field named.
/// It never sees <c>provenance</c> — that is parsed and enforced here.
/// </summary>
/// <typeparam name="TBody">The body record.</typeparam>
public sealed class EntityKind<TBody> : IEntityKind
    where TBody : class
{
    private readonly Func<JsonObject, TBody> _parseBody;

    /// <summary>Creates the kind.</summary>
    public EntityKind(string name, string collection, string skPrefix, Func<JsonObject, TBody> parseBody)
    {
        Name = name;
        Collection = collection;
        SkPrefix = skPrefix;
        _parseBody = parseBody;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Collection { get; }

    /// <inheritdoc />
    public string SkPrefix { get; }

    /// <summary>The body parser.</summary>
    public TBody ParseBody(JsonObject payload) => _parseBody(payload);

    /// <inheritdoc />
    public IEntityDraft ParseDraft(JsonObject payload, bool enforceProvenance = true) =>
        CaseEntityRules.ParseEntity(this, payload, enforceProvenance);

    /// <inheritdoc />
    public ICaseEntity Restore(string id, string caseId, DateTimeOffset createdAt, DateTimeOffset updatedAt, IEntityDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var typed = draft as EntityDraft<TBody> ?? throw new ArgumentException($"a {Name} draft was expected", nameof(draft));
        return new CaseEntity<TBody>(this, id, caseId, createdAt, updatedAt, typed.Body, typed.Provenance, typed.Amended);
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A validated body and its provenance, untyped.</summary>
public interface IEntityDraft
{
    /// <summary>The body.</summary>
    object Body { get; }

    /// <summary>The per-field provenance.</summary>
    ValueDictionary<string, ProvenanceEntry> Provenance { get; }

    /// <summary>Whether the record amends a filed one (issue #370).</summary>
    bool Amended { get; }

    /// <summary>The body as plain nested JSON.</summary>
    JsonObject BodyNode();
}

/// <summary>
/// A validated body plus its provenance, before the server stamps identity.
/// <see cref="Amended"/> (issue #370) rides beside the body, a GENERIC entity
/// attribute rather than a per-body field; whether it may be set at all is a
/// lifecycle rule the route enforces.
/// </summary>
/// <typeparam name="TBody">The body record.</typeparam>
public sealed record EntityDraft<TBody>(TBody Body, ValueDictionary<string, ProvenanceEntry> Provenance, bool Amended = false) : IEntityDraft
    where TBody : class
{
    object IEntityDraft.Body => Body;

    /// <inheritdoc />
    public JsonObject BodyNode() => BodyJson.ToNode(Body);
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

    /// <summary>Whether the record amends a filed one.</summary>
    bool Amended { get; }

    /// <summary>The body as plain nested JSON — what provenance paths address.</summary>
    JsonObject BodyNode();

    /// <summary>
    /// This record moved into another case (Copy case): same id, same
    /// <c>created_at</c>, the given <paramref name="updatedAt"/> and
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
    /// Validate a whole entity body; unknown keys are ignored. WHOLE, not
    /// partial: invariant 1 can only be checked against a complete record.
    /// The body is parsed BEFORE provenance, so a malformed field is reported
    /// first. <paramref name="enforceProvenance"/> is a WRITE rule, switched
    /// off on reads, so a rule tightening never makes stored rows unreadable.
    /// </summary>
    public static EntityDraft<TBody> ParseEntity<TBody>(EntityKind<TBody> kind, JsonObject payload, bool enforceProvenance = true)
        where TBody : class
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(payload);
        var body = kind.ParseBody(payload);
        var provenance = ProvenanceRules.Parse(payload["provenance"]);
        var amended = ParseAmended(payload);
        var draft = new EntityDraft<TBody>(body, provenance, amended);
        if (enforceProvenance)
        {
            // Against the record as it will be STORED: the parsers collapse
            // whitespace-only values to null, which then need no provenance.
            ProvenanceRules.RequireProvenance(draft.BodyNode(), provenance);
        }

        return draft;
    }

    /// <summary>
    /// A staff save may KEEP a <c>client_answered</c> entry and never mint one
    /// (<see cref="ProvenanceRules.RequireServerSourcesKept"/>). A null
    /// <paramref name="stored"/> is a create, which can keep nothing.
    /// </summary>
    public static void RequireServerSourcesKept<TBody>(EntityDraft<TBody> draft, CaseEntity<TBody>? stored)
        where TBody : class
    {
        ArgumentNullException.ThrowIfNull(draft);
        ProvenanceRules.RequireServerSourcesKept(draft.BodyNode(), draft.Provenance, stored?.BodyNode(), stored?.Provenance);
    }

    /// <summary>A new record: a fresh uuid, created and updated now.</summary>
    public static CaseEntity<TBody> Create<TBody>(EntityKind<TBody> kind, EntityDraft<TBody> draft, string caseId, TimeProvider? clock = null)
        where TBody : class
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.Now(clock);
        return new CaseEntity<TBody>(kind, Guid.NewGuid().ToString(), caseId, now, now, draft.Body, draft.Provenance, draft.Amended);
    }

    /// <summary>
    /// The draft's body over <paramref name="existing"/>, keeping its id (other
    /// records' provenance paths may name it) and <c>created_at</c> (the sort).
    /// </summary>
    public static CaseEntity<TBody> Replace<TBody>(CaseEntity<TBody> existing, EntityDraft<TBody> draft, TimeProvider? clock = null)
        where TBody : class
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(draft);
        return existing with
        {
            UpdatedAt = Clock.Now(clock),
            Body = draft.Body,
            Provenance = draft.Provenance,
            Amended = draft.Amended,
        };
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

    /// <summary>
    /// <c>amended</c>: shape and type only — true or false, defaulting false.
    /// Present-but-null is refused: the default covers an absent key only (<c>payload.get("amended", False)</c>).
    /// </summary>
    private static bool ParseAmended(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("amended", out var value))
        {
            return false;
        }

        return Json.AsBool(value)
            ?? throw new FieldValidationException(new Dictionary<string, string> { ["amended"] = "amended must be true or false." });
    }
}
