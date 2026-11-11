using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Notes;
using Insolvia.Domain.Provenance;

namespace Insolvia.Domain.Tests;

/// <summary>Case notes, translated from test_notes.py.</summary>
public sealed class NoteTests
{
    private const string CaseId = "case-0001";

    [Fact]
    public void Stamp_author_overwrites_whatever_the_caller_sent()
    {
        var spoofed = new NoteBody("note", AuthorSubject: Ids.Bob, AuthorName: "Bob Impersonator");
        var stamped = NoteRules.StampAuthor(spoofed, Ids.Alice, "Alice Attorney");
        Assert.Equal((Ids.Alice, "Alice Attorney", "note"), (stamped.AuthorSubject, stamped.AuthorName, stamped.Text));
    }

    [Fact]
    public void Note_carries_no_provenance_map() => Assert.Empty(Note().Provenance);

    [Fact]
    public void A_note_round_trips_through_its_stored_body()
    {
        var entity = Note(new NoteBody("note", "form/b106g"));
        Assert.Equal(entity.Body, BodyJson.Read(entity.BodyNode(), typeof(NoteBody)));
    }

    [Fact]
    public void May_edit_note_permits_the_author() => Assert.True(NoteRules.MayEdit(Note(), Ids.Alice, isAdmin: false));

    [Fact]
    public void May_edit_note_permits_a_firm_admin() => Assert.True(NoteRules.MayEdit(Note(), Ids.Bob, isAdmin: true));

    [Fact]
    public void May_edit_note_refuses_a_non_author_non_admin() => Assert.False(NoteRules.MayEdit(Note(), Ids.Bob, isAdmin: false));

    [Fact]
    public void A_note_is_not_a_generic_collection() => Assert.Null(CaseCollections.Find("notes"));

    private static CaseEntity<NoteBody> Note(NoteBody? body = null) =>
        CaseEntityRules.Create(NoteRules.Kind, CaseId, NoteRules.StampAuthor(body ?? new NoteBody("note"), Ids.Alice, "Alice Attorney"), ValueCollections.EmptyDictionary<string, ProvenanceEntry>());
}
