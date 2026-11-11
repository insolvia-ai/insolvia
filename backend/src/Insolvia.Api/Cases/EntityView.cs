using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Notes;
using Insolvia.Domain.Provenance;

namespace Insolvia.Api.Cases;

/// <summary>
/// A record of a case collection (<c>creditors</c>, <c>assets</c>, <c>claims</c>,
/// …): the server's own fields, and beside them at the top level the
/// collection's body fields, each unanswered one absent.
/// </summary>
/// <typeparam name="TBody">The collection's body record.</typeparam>
/// <param name="Id">The record's id, stable across saves.</param>
/// <param name="CaseId">The case.</param>
/// <param name="CreatedAt">First saved, ISO 8601 UTC.</param>
/// <param name="UpdatedAt">Last saved, ISO 8601 UTC.</param>
/// <param name="Provenance">Where each populated field came from, by body path.</param>
/// <param name="Amended">Whether this record amends a filed one; always present — "not amended" is an answer.</param>
/// <param name="Body">The collection's fields.</param>
[SnakeCase]
public sealed record EntityView<TBody>(
    string Id,
    string CaseId,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyDictionary<string, ProvenanceEntry> Provenance,
    bool Amended,
    [property: Flatten] TBody Body)
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
        return new(entity.Id, entity.CaseId, CaseRules.Iso(entity.CreatedAt), CaseRules.Iso(entity.UpdatedAt), entity.Provenance, entity.Amended, entity.Body);
    }

    /// <summary>The view of a record whose collection is known only at run time, for its own body type.</summary>
    /// <param name="entity">The stored record.</param>
    /// <returns>An <see cref="EntityView{TBody}"/>.</returns>
    public static object OfAny(ICaseEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return OfMethod.MakeGenericMethod(entity.Kind.BodyType).Invoke(null, [entity])!;
    }

    private static readonly System.Reflection.MethodInfo OfMethod =
        typeof(EntityViews).GetMethods().Single(method => method.Name == nameof(Of) && method.IsGenericMethodDefinition);
}

/// <summary>
/// A record of a case collection as written: the collection's body fields at
/// the top level, all optional (unknown keys are ignored), beside these.
/// Parsed by the collection's own parser, which owns validation; this record
/// is its shape for the OpenAPI document.
/// </summary>
/// <typeparam name="TBody">The collection's body record.</typeparam>
/// <param name="Body">The collection's fields; any may be absent (progressive intake).</param>
/// <param name="Provenance">Where each populated field came from, by body path; every populated field needs an entry.</param>
/// <param name="Amended">Whether this record amends a filed one — only once the case is filed. A PUT that omits it keeps the stored value.</param>
[SnakeCase]
public sealed record EntityRequest<TBody>(
    [property: Flatten] TBody? Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, ProvenanceEntry>? Provenance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Amended)
    where TBody : class;

/// <summary>A case note: the server's fields beside the note, its author stamped by the server.</summary>
/// <param name="Id">The note's id.</param>
/// <param name="CaseId">The case.</param>
/// <param name="CreatedAt">Written, ISO 8601 UTC.</param>
/// <param name="UpdatedAt">Last edited, ISO 8601 UTC.</param>
/// <param name="Body">The note; its unanswered fields are absent.</param>
[SnakeCase]
public sealed record NoteView(
    string Id,
    string CaseId,
    string CreatedAt,
    string UpdatedAt,
    [property: Flatten] NoteBody Body)
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

/// <summary>A note as written. The author is stamped by the server from the caller; any author fields sent are ignored.</summary>
/// <param name="Text">The note, at most 2000 characters.</param>
/// <param name="FormSeries">The form series the note is about, e.g. <c>form/b106ab</c>.</param>
[SnakeCase]
public sealed record NoteRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FormSeries);
