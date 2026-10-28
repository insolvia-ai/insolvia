using Insolvia.Api.Tests.Support;
using Insolvia.Core.Firms;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_library_creditors_routes.py: the firm's creditor library under
/// <c>/v1/firm/creditors</c>, gated by <c>creditor_library</c> and scoped to
/// the caller's firm (another firm's id is the same 404 as none).
/// </summary>
public sealed class LibraryCreditorRouteTests : IAsyncLifetime, IDisposable
{
    private const string Admin = Ids.Alice;
    private const string Viewer = Ids.Dana;
    private const string Blocked = Ids.Bob;
    private const string OtherAdmin = Ids.Sam;

    private readonly FirmWorld _world = new();

    public async ValueTask InitializeAsync()
    {
        static FirmUser Member(string subject, string firmId, PermissionLevel level, bool admin = false) =>
            FirmWorld.Member(subject, firmId, isAdmin: admin, permissions: new Dictionary<Feature, PermissionLevel> { [Feature.CreditorLibrary] = level });

        await (await _world.WithFirmsAsync()).WithAsync(
            Member(Admin, Ids.FirmA, PermissionLevel.AddEdit, admin: true),
            Member(Viewer, Ids.FirmA, PermissionLevel.ViewOnly),
            Member(Blocked, Ids.FirmA, PermissionLevel.Hidden),
            Member(OtherAdmin, Ids.FirmB, PermissionLevel.AddEdit, admin: true));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── Gating ───────────────────────────────────────────────────────

    [Fact]
    public async Task View_only_can_list_but_not_add()
    {
        Assert.Equal(200, (await _world.Get("/v1/firm/creditors", Viewer)).Status);
        Assert.Equal(403, (await Add(subject: Viewer)).Status);
    }

    [Fact]
    public async Task Hidden_cannot_even_list()
    {
        Assert.Equal(403, (await _world.Get("/v1/firm/creditors", Blocked)).Status);
    }

    [Fact]
    public async Task No_auth_is_401()
    {
        Assert.Equal(ErrorBodies.Unauthorized, (await _world.Get("/v1/firm/creditors")).Body);
    }

    // ── CRUD ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_created_creditor_is_returned_and_then_listed()
    {
        var created = await Add(extra: """, "address": {"line1": "1 Main St", "city": "Springfield"}""");

        Assert.Equal(201, created.Status);
        var body = created.Object;
        Assert.Equal("Acme Collections", body["name"]!.GetValue<string>());
        Assert.Equal("1 Main St", body["address"]!["line1"]!.GetValue<string>());
        Assert.False(body["preferred"]!.GetValue<bool>());

        // `notes` omitted when absent; `address` and the parties always present.
        Assert.Equal(["additional_notice_parties", "address", "created_at", "id", "name", "preferred", "updated_at"], Answer.Keys(created.Json));

        var listed = (await _world.Get("/v1/firm/creditors", Admin)).Object["creditors"]!.AsArray();
        Assert.Equal([created.Str("id")], listed.Select(c => c!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_missing_name_is_a_400_naming_the_field()
    {
        var answer = await _world.Post("/v1/firm/creditors", "{}", Admin);

        Assert.Equal(400, answer.Status);
        Assert.Contains("name", answer.FieldErrors);
    }

    [Fact]
    public async Task Get_one_by_id()
    {
        var created = await Add();
        var fetched = await _world.Get($"/v1/firm/creditors/{created.Str("id")}", Admin);

        Assert.Equal(200, fetched.Status);
        Assert.Equal(created.Body, fetched.Body);
    }

    [Fact]
    public async Task A_replace_keeps_the_id_and_changes_the_fields()
    {
        var created = await Add(extra: """, "address": {"line1": "1 Main St"}, "notes": "Call first." """);

        var replaced = await _world.Put($"/v1/firm/creditors/{created.Str("id")}", """{"name": "Acme Collections LLC", "preferred": true}""", Admin);

        Assert.Equal(200, replaced.Status);
        var body = replaced.Object;
        Assert.Equal(created.Str("id"), body["id"]!.GetValue<string>());
        Assert.Equal("Acme Collections LLC", body["name"]!.GetValue<string>());
        Assert.True(body["preferred"]!.GetValue<bool>());

        // PUT, whole-record: no address CLEARS it, and no notes drops the key.
        Assert.Equal("{}", body["address"]!.ToJsonString());
        Assert.False(body.ContainsKey("notes"));
    }

    [Fact]
    public async Task Deleting_removes_it_from_the_list()
    {
        var created = await Add();

        var deleted = await _world.Delete($"/v1/firm/creditors/{created.Str("id")}", Admin);

        Assert.Equal(204, deleted.Status);
        Assert.Equal("", deleted.Body);
        Assert.Equal("""{"creditors":[]}""", (await _world.Get("/v1/firm/creditors", Admin)).Body);
    }

    [Fact]
    public async Task Deleting_twice_is_404_the_second_time()
    {
        var created = await Add();
        await _world.Delete($"/v1/firm/creditors/{created.Str("id")}", Admin);

        var again = await _world.Delete($"/v1/firm/creditors/{created.Str("id")}", Admin);

        Assert.Equal(404, again.Status);
        Assert.Equal(HttpAssert.ErrorBody("NotFoundError", "library creditor not found"), again.Body);
    }

    // ── Firm scoping (the anti-oracle rule) ──────────────────────────

    [Fact]
    public async Task Another_firms_creditor_is_a_404_not_a_403()
    {
        var created = await Add();
        var url = $"/v1/firm/creditors/{created.Str("id")}";

        Assert.Equal(404, (await _world.Get(url, OtherAdmin)).Status);
        Assert.Equal(404, (await _world.Put(url, """{"name": "Hijacked"}""", OtherAdmin)).Status);
        Assert.Equal(404, (await _world.Delete(url, OtherAdmin)).Status);

        var stillThere = await _world.Get(url, Admin);
        Assert.Equal(200, stillThere.Status);
        Assert.Equal("Acme Collections", stillThere.Str("name"));
    }

    [Fact]
    public async Task Additional_notice_parties_round_trip()
    {
        var created = await Add(extra: """, "additional_notice_parties": [{"id": "np1", "name": "Legal Dept", "account_last4": "1234"}]""");

        Assert.Equal(
            """[{"account_last4":"1234","address":{},"id":"np1","name":"Legal Dept"}]""",
            created.Object["additional_notice_parties"]!.ToJsonString());
    }

    [Fact]
    public async Task A_bad_notice_party_is_reported_under_the_field_the_caller_sent()
    {
        var answer = await Add(extra: """, "additional_notice_parties": [{"name": "No id"}]""");

        Assert.Equal(400, answer.Status);
        Assert.Contains("additional_notice_parties[0].id", answer.FieldErrors);
    }

    private Task<Answer> Add(string name = "Acme Collections", string subject = Admin, string extra = "") =>
        _world.Post("/v1/firm/creditors", $$"""{"name": "{{name}}"{{extra}}}""", subject);
}
