using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Notes;

/// <summary>
/// A case note's data, including the author snapshot captured at write (issue
/// 14.5 / #357; from <c>notes.py</c>) — a colleague renamed later must not
/// change what an old note says. <see cref="FormSeries"/> anchors the note to
/// a form, unvalidated against the packet. A note uses the case-entity
/// machinery but is NOT a generic collection: server-stamped authorship,
/// per-record edit ownership, no provenance. A request's author members are
/// never trusted: <see cref="NoteRules.StampAuthor"/> overwrites them.
/// </summary>
public sealed record NoteBody(
    [property: Text(2000)] string? Text = null,
    [property: Text] string? FormSeries = null,
    [property: Text] string? AuthorSubject = null,
    [property: Text] string? AuthorName = null);

/// <summary>The note rules.</summary>
public static class NoteRules
{
    /// <summary>The note kind — deliberately absent from <see cref="CaseCollections"/>.</summary>
    public static readonly EntityKind<NoteBody> Kind = new("note", "notes", "NOTE");

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
