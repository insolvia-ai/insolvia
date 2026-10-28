using System.Text.Json.Nodes;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Notes;

/// <summary>
/// A case note's data, including the author snapshot captured at write (issue
/// 14.5 / #357; from <c>notes.py</c>) — a colleague renamed later must not
/// change what an old note says. <see cref="FormSeries"/> anchors the note to
/// a form, unvalidated against the packet. A note uses the case-entity
/// machinery but is NOT a generic collection: server-stamped authorship,
/// per-record edit ownership, no provenance.
/// </summary>
public sealed record NoteBody(string? Text, string? FormSeries, string? AuthorSubject, string? AuthorName);

/// <summary>The note rules.</summary>
public static class NoteRules
{
    /// <summary>The note kind — deliberately absent from <see cref="CaseCollections"/>.</summary>
    public static readonly EntityKind<NoteBody> Kind = new("note", "notes", "NOTE", Parse);

    /// <summary>
    /// Validates a note body. The author members ARE read — a stored note is
    /// rebuilt through this — but a request's are never trusted:
    /// <see cref="StampAuthor"/> overwrites them before anything is saved.
    /// </summary>
    public static NoteBody Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new NoteBody(
            Text: FieldParsers.Text(payload["text"], "text", errors, limit: 2000),
            FormSeries: FieldParsers.Text(payload["form_series"], "form_series", errors),
            AuthorSubject: FieldParsers.Text(payload["author_subject"], "author_subject", errors),
            AuthorName: FieldParsers.Text(payload["author_name"], "author_name", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>A copy of <paramref name="body"/> carrying who wrote it — the ONE place authorship is assigned.</summary>
    public static NoteBody StampAuthor(NoteBody body, string subject, string authorName)
    {
        ArgumentNullException.ThrowIfNull(body);
        return body with { AuthorSubject = subject, AuthorName = authorName };
    }

    /// <summary>Whether <paramref name="subject"/> may PUT or DELETE this note: its author, or a firm admin.</summary>
    public static bool MayEdit(CaseEntity<NoteBody> note, string subject, bool isAdmin)
    {
        ArgumentNullException.ThrowIfNull(note);
        return isAdmin || note.Body.AuthorSubject == subject;
    }
}
