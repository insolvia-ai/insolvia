using Insolvia.Core.CaseEntities;
using Insolvia.Core.Notes;
using Insolvia.Core.Provenance;

namespace Insolvia.Core.Tests;

/// <summary>Case notes, translated from test_notes.py.</summary>
public sealed class NoteTests
{
    private const string CaseId = "case-0001";

    [Fact]
    public void Parse_note_body_reads_text_and_form_series()
    {
        var body = NoteRules.Parse(Body.Of("""{"text": "Client called about the car loan.", "form_series": "form/b106g"}"""));
        Assert.Equal(("Client called about the car loan.", "form/b106g"), (body.Text, body.FormSeries));
        Assert.Null(body.AuthorSubject);
        Assert.Null(body.AuthorName);
    }

    [Fact]
    public void Parse_note_body_reads_author_fields_when_present()
    {
        var body = NoteRules.Parse(Body.Of($$"""{"text": "note", "author_subject": "{{Ids.Alice}}", "author_name": "Alice Attorney"}"""));
        Assert.Equal((Ids.Alice, "Alice Attorney"), (body.AuthorSubject, body.AuthorName));
    }

    [Fact]
    public void Stamp_author_overwrites_whatever_the_parser_saw()
    {
        var spoofed = NoteRules.Parse(Body.Of($$"""{"text": "note", "author_subject": "{{Ids.Bob}}", "author_name": "Bob Impersonator"}"""));
        var stamped = NoteRules.StampAuthor(spoofed, Ids.Alice, "Alice Attorney");
        Assert.Equal((Ids.Alice, "Alice Attorney"), (stamped.AuthorSubject, stamped.AuthorName));
    }

    [Fact]
    public void Parse_note_body_allows_an_absent_form_series() =>
        Assert.Null(NoteRules.Parse(Body.Of("""{"text": "A case-level note."}""")).FormSeries);

    [Fact]
    public void Parse_note_body_rejects_non_text_value() =>
        Assert.Contains("text", Assert.Throws<FieldValidationException>(() => NoteRules.Parse(Body.Of("""{"text": 12345}"""))).Fields.Keys);

    [Fact]
    public void Stamp_author_fills_in_the_caller()
    {
        var stamped = NoteRules.StampAuthor(NoteRules.Parse(Body.Of("""{"text": "note"}""")), Ids.Alice, "Alice Attorney");
        Assert.Equal((Ids.Alice, "Alice Attorney", "note"), (stamped.AuthorSubject, stamped.AuthorName, stamped.Text));
    }

    [Fact]
    public void Note_carries_no_provenance_map() => Assert.Empty(Note().Provenance);

    // test_note_json_shape: the JSON view; dropped. test_note_round_trips_through_the_stored_item_shape
    // becomes the stored body's round trip:
    [Fact]
    public void A_note_round_trips_through_its_stored_body()
    {
        var entity = Note("""{"text": "note", "form_series": "form/b106g"}""");
        Assert.Equal("NOTE", NoteRules.Kind.SkPrefix);
        Assert.Equal(entity.Body, NoteRules.Parse(BodyJson.Prune(entity.BodyNode())));
    }

    [Fact]
    public void May_edit_note_permits_the_author() => Assert.True(NoteRules.MayEdit(Note(), Ids.Alice, isAdmin: false));

    [Fact]
    public void May_edit_note_permits_a_firm_admin() => Assert.True(NoteRules.MayEdit(Note(), Ids.Bob, isAdmin: true));

    [Fact]
    public void May_edit_note_refuses_a_non_author_non_admin() => Assert.False(NoteRules.MayEdit(Note(), Ids.Bob, isAdmin: false));

    [Fact]
    public void Editing_a_note_can_change_the_text_without_changing_the_author()
    {
        var entity = Note("""{"text": "first draft"}""");
        var editedByAdmin = NoteRules.StampAuthor(NoteRules.Parse(Body.Of("""{"text": "corrected draft"}""")), entity.Body.AuthorSubject ?? Ids.Bob, entity.Body.AuthorName ?? "someone else");
        var replaced = CaseEntityRules.Replace(entity, new EntityDraft<NoteBody>(editedByAdmin, ValueCollections.EmptyDictionary<string, ProvenanceEntry>()));
        Assert.Equal(("corrected draft", Ids.Alice, "Alice Attorney"), (replaced.Body.Text, replaced.Body.AuthorSubject, replaced.Body.AuthorName));
        Assert.Equal((entity.Id, entity.CreatedAt), (replaced.Id, replaced.CreatedAt));
    }

    [Fact]
    public void A_note_is_not_a_generic_collection() => Assert.Null(CaseCollections.Find("notes"));

    private static CaseEntity<NoteBody> Note(string json = """{"text": "note"}""")
    {
        var body = NoteRules.StampAuthor(NoteRules.Parse(Body.Of(json)), Ids.Alice, "Alice Attorney");
        return CaseEntityRules.Create(NoteRules.Kind, new EntityDraft<NoteBody>(body, ValueCollections.EmptyDictionary<string, ProvenanceEntry>()), CaseId);
    }
}
