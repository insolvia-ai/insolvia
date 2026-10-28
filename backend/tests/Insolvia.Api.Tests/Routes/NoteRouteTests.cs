using Insolvia.Api.Tests.Support;
using Insolvia.Core.Firms;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_notes_routes.py: case notes — gated by <c>notes</c>, the author stamped
/// by the server, edits and deletes the author's or a firm admin's alone,
/// newest first. The author here is Dana (access to every case, not an admin);
/// the admin is Alice; the colleague who is neither is Bob, linked to the case.
/// </summary>
public sealed class NoteRouteTests : IAsyncLifetime, IDisposable
{
    private readonly CaseWorld _world = new();
    private string _caseId = "";

    public async ValueTask InitializeAsync()
    {
        await _world.SeedAsync();
        _caseId = await _world.OpenCaseIdAsync();
        await _world.Put($"/v1/cases/{_caseId}/assignees/{Bob}", null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("POST", "/v1/cases/any-id/notes")]
    [InlineData("GET", "/v1/cases/any-id/notes")]
    [InlineData("PUT", "/v1/cases/any-id/notes/any-note")]
    [InlineData("DELETE", "/v1/cases/any-id/notes/any-note")]
    public async Task Every_route_refuses_an_unauthenticated_caller(string method, string path)
    {
        Assert.Equal(401, (await _world.World.SendAsync(new HttpMethod(method), path, json: "{}")).Status);
    }

    [Fact]
    public async Task Another_firms_case_is_not_found_on_write_or_read()
    {
        await Add();

        Assert.Equal(404, (await Add(subject: Carol)).Status);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{_caseId}/notes", Carol)).Status);
    }

    [Fact]
    public async Task A_role_without_the_notes_feature_is_refused()
    {
        await _world.ReplaceAsync(FirmWorld.Member(Greg, role: FirmRole.Staff, accessAllCases: true));

        Assert.Equal(403, (await Add(subject: Greg)).Status);
        Assert.Equal(403, (await _world.Get($"/v1/cases/{_caseId}/notes", Greg)).Status);
    }

    [Fact]
    public async Task Creating_a_note_stamps_the_caller_as_author_and_ignores_a_supplied_one()
    {
        var answer = await Add(""" "text": "Client called.", "author_subject": "00000000-0000-4000-8000-00000000b0b0", "author_name": "Bob Impersonator" """, Dana);

        Assert.Equal(201, answer.Status);
        Assert.Equal(("Client called.", Dana, "Person da4a", _caseId), (answer.Str("text"), answer.Str("author_subject"), answer.Str("author_name"), answer.Str("case_id")));
        Assert.Equal(["author_name", "author_subject", "case_id", "created_at", "id", "text", "updated_at"], Answer.Keys(answer.Json));
    }

    [Fact]
    public async Task A_note_can_anchor_to_a_form_series()
    {
        Assert.Equal("form/b106g", (await Add(""" "text": "Check the lease term.", "form_series": "form/b106g" """)).Str("form_series"));
    }

    [Fact]
    public async Task Listing_returns_notes_newest_first()
    {
        var first = (await Add(""" "text": "first" """)).Str("id");
        var second = (await Add(""" "text": "second" """)).Str("id");

        var listed = (await _world.Get($"/v1/cases/{_caseId}/notes")).Object["notes"]!.AsArray();

        Assert.Equal([second, first], listed.Select(n => n!.AsObject().Str("id")));
    }

    [Fact]
    public async Task The_author_and_a_firm_admin_may_edit_and_the_original_author_stays()
    {
        var noteId = (await Add(""" "text": "first draft" """, Dana)).Str("id");

        var own = await _world.Put($"/v1/cases/{_caseId}/notes/{noteId}", """{"text": "corrected draft"}""", Dana);
        var admin = await _world.Put($"/v1/cases/{_caseId}/notes/{noteId}", """{"text": "corrected by an admin"}""");

        Assert.Equal((200, "corrected draft"), (own.Status, own.Str("text")));
        Assert.Equal((200, "corrected by an admin", Dana, "Person da4a"), (admin.Status, admin.Str("text"), admin.Str("author_subject"), admin.Str("author_name")));
    }

    [Fact]
    public async Task Neither_author_nor_admin_may_edit_or_delete()
    {
        var noteId = (await Add(subject: Dana)).Str("id");

        Assert.Equal(
            """{"error":"ForbiddenError","message":"only the author or a firm admin may change this note"}""",
            (await _world.Put($"/v1/cases/{_caseId}/notes/{noteId}", """{"text": "should not land"}""", Bob)).Body);
        Assert.Equal(403, (await _world.Delete($"/v1/cases/{_caseId}/notes/{noteId}", Bob)).Status);
        Assert.Single((await _world.Get($"/v1/cases/{_caseId}/notes", Bob)).Object["notes"]!.AsArray());
    }

    [Fact]
    public async Task The_author_and_a_firm_admin_may_delete()
    {
        var own = (await Add(subject: Dana)).Str("id");
        var other = (await Add(subject: Dana)).Str("id");

        Assert.Equal(204, (await _world.Delete($"/v1/cases/{_caseId}/notes/{own}", Dana)).Status);
        Assert.Equal(204, (await _world.Delete($"/v1/cases/{_caseId}/notes/{other}")).Status);
        Assert.Equal("""{"notes":[]}""", (await _world.Get($"/v1/cases/{_caseId}/notes")).Body);
    }

    [Fact]
    public async Task Editing_an_id_the_server_never_minted_is_a_404()
    {
        Assert.Equal("""{"error":"NotFoundError","message":"note not found"}""", (await _world.Put($"/v1/cases/{_caseId}/notes/never-minted", """{"text": "x"}""")).Body);
    }

    [Fact]
    public async Task Notes_are_not_a_generic_collection()
    {
        await Add();

        Assert.Single((await _world.Get($"/v1/cases/{_caseId}/notes")).Object["notes"]!.AsArray());
        Assert.Equal(404, (await _world.Get($"/v1/cases/{_caseId}/notes/any")).Status);
    }

    private Task<Answer> Add(string body = """ "text": "Called the client." """, string subject = Alice) =>
        _world.Post($"/v1/cases/{_caseId}/notes", "{" + body + "}", subject);
}
