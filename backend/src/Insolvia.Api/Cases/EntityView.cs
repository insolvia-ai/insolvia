using System.Text.Json;
using System.Text.Json.Serialization;
using Insolvia.Api.Http;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Notes;
using Insolvia.Domain.Provenance;

namespace Insolvia.Api.Cases;

/// <summary>
/// A record of one of the generic case collections (<c>creditors</c>,
/// <c>assets</c>, <c>claims</c>, …): the server's own fields, and beside them
/// at the top level the collection's body fields, each unanswered one absent.
/// Which body fields there are depends on the collection in the URL.
/// </summary>
/// <param name="Id">The record's id, stable across saves.</param>
/// <param name="CaseId">The case.</param>
/// <param name="CreatedAt">First saved, ISO 8601 UTC.</param>
/// <param name="UpdatedAt">Last saved, ISO 8601 UTC.</param>
/// <param name="Provenance">Where each populated field came from, by body path.</param>
/// <param name="Amended">Whether this record amends a filed one; always present — "not amended" is an answer.</param>
[SnakeCase]
public sealed record EntityView(
    string Id,
    string CaseId,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyDictionary<string, ProvenanceEntry> Provenance,
    bool Amended)
{
    /// <summary>The collection's body fields, written beside the server's.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement> Body { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>The view of <paramref name="entity"/>.</summary>
    /// <param name="entity">The stored record.</param>
    /// <returns>The view.</returns>
    public static EntityView Of(ICaseEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // The body's own record type, through the wire rules: its unanswered members absent.
        var body = JsonSerializer.SerializeToElement(entity.Body, entity.Body.GetType(), WireJson.Options).EnumerateObject().ToDictionary(member => member.Name, member => member.Value, StringComparer.Ordinal);
        return new(entity.Id, entity.CaseId, CaseRules.Iso(entity.CreatedAt), CaseRules.Iso(entity.UpdatedAt), entity.Provenance, entity.Amended) { Body = body };
    }
}

/// <summary>
/// A record of a generic case collection as written: the collection's body
/// fields at the top level (which ones depends on the collection in the URL;
/// unknown keys are ignored), beside these. Parsed by the collection's own
/// parser, which owns validation; this record is its shape for the OpenAPI document.
/// </summary>
/// <param name="Provenance">Where each populated field came from, by body path; every populated field needs an entry.</param>
/// <param name="Amended">Whether this record amends a filed one — only once the case is filed. A PUT that omits it keeps the stored value.</param>
[SnakeCase]
public sealed record EntityRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, ProvenanceEntry>? Provenance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Amended)
{
    /// <summary>The collection's body fields.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Body { get; init; }
}

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
