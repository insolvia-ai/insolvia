using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Fields;
using Insolvia.Domain.Notes;
using Insolvia.Domain.Provenance;

namespace Insolvia.Api.Cases;

/// <summary>
/// A record of a case collection (<c>creditors</c>, <c>assets</c>, <c>claims</c>,
/// …): the server's own fields, the collection's body under <c>body</c> with
/// each unanswered field absent, and where each field came from.
/// </summary>
/// <typeparam name="TBody">The collection's body record.</typeparam>
/// <param name="Id">The record's id, stable across saves.</param>
/// <param name="CaseId">The case.</param>
/// <param name="CreatedAt">First saved, ISO 8601 UTC.</param>
/// <param name="UpdatedAt">Last saved, ISO 8601 UTC.</param>
/// <param name="Body">The collection's fields.</param>
/// <param name="Provenance">Where each populated field came from, by body path (<c>address.postalCode</c>).</param>
/// <param name="Amended">Whether this record amends a filed one; always present — "not amended" is an answer.</param>
public sealed record EntityView<TBody>(
    string Id,
    string CaseId,
    string CreatedAt,
    string UpdatedAt,
    TBody Body,
    IReadOnlyDictionary<string, ProvenanceEntry> Provenance,
    bool Amended)
    where TBody : class;

/// <summary>The views of stored collection records.</summary>
public static class EntityViews
{
    /// <summary>The view of <paramref name="entity"/>.</summary>
    /// <typeparam name="TBody">The collection's body record.</typeparam>
    /// <param name="entity">The stored record.</param>
    /// <returns>The view.</returns>
    public static EntityView<TBody> Of<TBody>(CaseEntity<TBody> entity)
        where TBody : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new(entity.Id, entity.CaseId, CaseRules.Iso(entity.CreatedAt), CaseRules.Iso(entity.UpdatedAt), entity.Body, entity.Provenance, entity.Amended);
    }

    /// <summary>The view of a record whose collection is known only at run time; its body is written as its own type.</summary>
    /// <param name="entity">The stored record.</param>
    /// <returns>The view.</returns>
    public static EntityView<object> OfAny(ICaseEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new(entity.Id, entity.CaseId, CaseRules.Iso(entity.CreatedAt), CaseRules.Iso(entity.UpdatedAt), entity.Body, entity.Provenance, entity.Amended);
    }
}

/// <summary>
/// A record of a case collection as written: its body, where each populated
/// field came from, and whether it amends a filed record.
/// </summary>
/// <typeparam name="TBody">The collection's body record.</typeparam>
/// <param name="Body">The collection's fields; any may be absent (progressive intake), and an absent body is an empty one. Unknown fields are ignored.</param>
/// <param name="Provenance">Where each populated field came from, by body path; every populated field needs an entry.</param>
/// <param name="Amended">Whether this record amends a filed one — only once the case is filed. A PUT that omits it keeps the stored value.</param>
public sealed record EntityRequest<TBody>(
    TBody? Body = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, ProvenanceEntry>? Provenance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Amended = null)
    where TBody : class
{
    // Never Required: a 400 for a missing body would answer before the route's auth does.
    private static readonly TBody Empty = (TBody)BodyJson.Read(new System.Text.Json.Nodes.JsonObject(), typeof(TBody));

    /// <summary>The collection's fields.</summary>
    public TBody Body { get; init; } = Body ?? Empty;
}

/// <summary>A case note: the server's fields, and the note with its author stamped by the server.</summary>
/// <param name="Id">The note's id.</param>
/// <param name="CaseId">The case.</param>
/// <param name="CreatedAt">Written, ISO 8601 UTC.</param>
/// <param name="UpdatedAt">Last edited, ISO 8601 UTC.</param>
/// <param name="Body">The note; its unanswered fields are absent.</param>
public sealed record NoteView(string Id, string CaseId, string CreatedAt, string UpdatedAt, NoteBody Body)
{
    /// <summary>The view of <paramref name="note"/>.</summary>
    /// <param name="note">The stored note.</param>
    /// <returns>The view.</returns>
    public static NoteView Of(CaseEntity<NoteBody> note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return new(note.Id, note.CaseId, CaseRules.Iso(note.CreatedAt), CaseRules.Iso(note.UpdatedAt), note.Body);
    }
}

/// <summary>A case's notes, newest first.</summary>
/// <param name="Notes">The notes.</param>
public sealed record NoteList(IReadOnlyList<NoteView> Notes);

/// <summary>A note as written. The author is stamped by the server from the caller.</summary>
/// <param name="Text">The note, one line of at most 2000 characters.</param>
/// <param name="FormSeries">The form series the note is about, e.g. <c>form/b106ab</c>.</param>
public sealed record NoteRequest(
    [property: Text(2000), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: Text, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FormSeries = null);
